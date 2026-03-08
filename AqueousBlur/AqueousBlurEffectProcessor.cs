using OpenCvSharp;
using SharpGen.Runtime;
using System;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Vortice.DCommon;
using Vortice.Direct2D1;
using Vortice.Direct2D1.Effects;
using Vortice.DXGI;
using Vortice.Mathematics;
using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Player.Video;

#nullable enable
namespace AqueousBlur
{
    internal class AqueousBlurEffectProcessor : IVideoEffectProcessor, IDrawable, IDisposable
    {
        private readonly DisposeCollector disposer = new DisposeCollector();
        private readonly IGraphicsDevicesAndContext devices;
        private readonly AqueousBlurEffect item;
        private readonly AffineTransform2D wrap;
        public ID2D1Image Output { get; }

        private ID2D1Image? inputCurrent;
        private Mat _grayCurr = new Mat();

        private string _loadedImagePath = string.Empty;
        private string _loadedVideoPath = string.Empty;
        private Mat? _externalMapCache;
        private VideoCapture? _videoCapture;
        private ID2D1Bitmap1? _outputBitmapCache;

        public AqueousBlurEffectProcessor(IGraphicsDevicesAndContext devices, AqueousBlurEffect item)
        {
            this.devices = devices;
            this.item = item;
            this.wrap = new AffineTransform2D((ID2D1DeviceContext)devices.DeviceContext);
            this.disposer.Collect(this.wrap);
            this.Output = this.wrap.Output;
            this.disposer.Collect(this.Output);
        }

        public DrawDescription Update(EffectDescription effectDescription)
        {
            if (this.inputCurrent == null)
            {
                this.wrap.SetInput(0, null, true);
                return effectDescription.DrawDescription;
            }

            var dc = (ID2D1DeviceContext)this.devices.DeviceContext;
            int frame = effectDescription.ItemPosition.Frame;
            int duration = effectDescription.ItemDuration.Frame;
            int fps = effectDescription.FPS;

            double amount = this.item.Amount.GetValue(frame, duration, fps);
            int samples = Math.Max(1, (int)this.item.Samples.GetValue(frame, duration, fps));
            double mapSoftness = this.item.MapSoftness.GetValue(frame, duration, fps);
            double ridgeSmoothness = this.item.RidgeSmoothness.GetValue(frame, duration, fps);
            double angleOffset = this.item.AngleOffset.GetValue(frame, duration, fps);
            var blurType = this.item.Type;

            // --- 外部マップ読み込み ---
            if (this.item.MapSource == AqueousBlurEffect.MapSourceType.ExternalFile)
            {
                string path = this.item.MapFilePath ?? string.Empty;
                string ext = System.IO.Path.GetExtension(path).ToLower();
                bool isVideo = ext == ".mp4" || ext == ".avi" || ext == ".mov" || ext == ".wmv" || ext == ".webm" || ext == ".mkv";
                if (isVideo)
                {
                    if (_loadedVideoPath != path)
                    {
                        _loadedVideoPath = path;
                        _videoCapture?.Dispose();
                        _videoCapture = new VideoCapture(path);
                    }
                    if (_videoCapture != null && _videoCapture.IsOpened())
                    {
                        double videoFps = _videoCapture.Get(VideoCaptureProperties.Fps);
                        double totalFrames = _videoCapture.Get(VideoCaptureProperties.FrameCount);
                        double timeSec = (double)frame / fps;
                        if (videoFps > 0 && totalFrames > 0) timeSec = timeSec % (totalFrames / videoFps);
                        _videoCapture.Set(VideoCaptureProperties.PosMsec, timeSec * 1000.0);
                        _externalMapCache?.Dispose();
                        _externalMapCache = new Mat();
                        if (!_videoCapture.Read(_externalMapCache) || _externalMapCache.Empty())
                        { _externalMapCache?.Dispose(); _externalMapCache = null; }
                    }
                }
                else
                {
                    if (_loadedImagePath != path)
                    {
                        _loadedImagePath = path;
                        _externalMapCache?.Dispose(); _externalMapCache = null;
                        using var mapBmp = LoadWicBitmap(dc, _loadedImagePath);
                        if (mapBmp != null) _externalMapCache = BitmapToOpenCvMat(dc, mapBmp);
                    }
                }
            }

            using var currentBitmap = ConvertToBitmap(dc, this.inputCurrent, out var offset);
            ID2D1Image resultImage = currentBitmap;

            if (Math.Abs(amount) > 0.1)
            {
                using var matCurr = BitmapToOpenCvMat(dc, currentBitmap);
                Mat targetMat = matCurr;
                bool needDisposeTarget = false;

                if (this.item.MapSource == AqueousBlurEffect.MapSourceType.ExternalFile && _externalMapCache != null && !_externalMapCache.Empty())
                {
                    targetMat = new Mat();
                    Cv2.Resize(_externalMapCache, targetMat, new OpenCvSharp.Size(matCurr.Cols, matCurr.Rows));
                    needDisposeTarget = true;
                }

                if (targetMat.Channels() == 4) Cv2.CvtColor(targetMat, _grayCurr, ColorConversionCodes.BGRA2GRAY);
                else if (targetMat.Channels() == 3) Cv2.CvtColor(targetMat, _grayCurr, ColorConversionCodes.BGR2GRAY);
                else targetMat.CopyTo(_grayCurr);
                if (needDisposeTarget) targetMat.Dispose();

                using var grayFloat = new Mat();
                _grayCurr.ConvertTo(grayFloat, MatType.CV_32F, 1.0 / 255.0);

                if (mapSoftness > 0.1)
                {
                    double sigma = mapSoftness / 3.0;
                    int ksize = ((int)Math.Ceiling(sigma * 3.0)) * 2 + 1;
                    Cv2.GaussianBlur(grayFloat, grayFloat, new OpenCvSharp.Size(ksize, ksize), sigma);
                }

                int w = matCurr.Cols, h = matCurr.Rows;

                // ===================================================================
                // Direction Center / Direction Fading:
                //   勾配ベクトルではなく、マップの明るさを角度として解釈
                //   明るさ 0.0→0°, 0.5→180°, 1.0→360° として方向ベクトルを生成
                //
                // Natural / Constant Length / Perpendicular:
                //   Sobel勾配ベクトルを使用（従来通り）
                // ===================================================================
                using var flowX = new Mat(h, w, MatType.CV_32F);
                using var flowY = new Mat(h, w, MatType.CV_32F);
                float[] magArr = new float[w * h];

                double angleRad = angleOffset * Math.PI / 180.0;
                float cosA = (float)Math.Cos(angleRad);
                float sinA = (float)Math.Sin(angleRad);

                if (blurType == AqueousBlurEffect.BlurType.DirectionCenter ||
                    blurType == AqueousBlurEffect.BlurType.DirectionFading)
                {
                    // マップの明るさ → 角度
                    unsafe
                    {
                        float* pGray = (float*)grayFloat.DataPointer;
                        float* pFX = (float*)flowX.DataPointer;
                        float* pFY = (float*)flowY.DataPointer;
                        for (int i = 0; i < w * h; i++)
                        {
                            float val = pGray[i];
                            float angle = val * MathF.PI * 2.0f; // 0〜2πにマッピング
                            float dx = MathF.Cos(angle);
                            float dy = MathF.Sin(angle);
                            // Angle Offset適用
                            pFX[i] = dx * cosA - dy * sinA;
                            pFY[i] = dx * sinA + dy * cosA;
                            magArr[i] = 1.0f; // Direction系は常にフル強度
                        }
                    }
                }
                else
                {
                    // Natural / Constant Length / Perpendicular: Sobel勾配
                    using var gradX = new Mat();
                    using var gradY = new Mat();
                    Cv2.Sobel(grayFloat, gradX, MatType.CV_32F, 1, 0, 3, 1.0);
                    Cv2.Sobel(grayFloat, gradY, MatType.CV_32F, 0, 1, 3, 1.0);

                    bool isPerpendicular = (blurType == AqueousBlurEffect.BlurType.Perpendicular);

                    unsafe
                    {
                        float* pGX = (float*)gradX.DataPointer;
                        float* pGY = (float*)gradY.DataPointer;
                        float* pFX = (float*)flowX.DataPointer;
                        float* pFY = (float*)flowY.DataPointer;
                        for (int i = 0; i < w * h; i++)
                        {
                            float gx = pGX[i], gy = pGY[i];

                            float bx, by;
                            if (isPerpendicular)
                            {
                                // Perpendicular: 勾配に垂直な方向（等高線に沿う）
                                bx = -gy;
                                by = gx;
                            }
                            else
                            {
                                bx = gx;
                                by = gy;
                            }

                            // Angle Offset適用
                            pFX[i] = bx * cosA - by * sinA;
                            pFY[i] = bx * sinA + by * cosA;
                            magArr[i] = MathF.Sqrt(pFX[i] * pFX[i] + pFY[i] * pFY[i]);
                        }
                    }
                }

                // Ridge Smoothness（Direction系を除く勾配ベースの場合のみ意味がある）
                if (ridgeSmoothness > 0.1)
                {
                    double sigma = ridgeSmoothness;
                    int ksize = ((int)Math.Ceiling(sigma * 3.0)) * 2 + 1;
                    Cv2.GaussianBlur(flowX, flowX, new OpenCvSharp.Size(ksize, ksize), sigma);
                    Cv2.GaussianBlur(flowY, flowY, new OpenCvSharp.Size(ksize, ksize), sigma);
                    unsafe
                    {
                        float* pFX = (float*)flowX.DataPointer;
                        float* pFY = (float*)flowY.DataPointer;
                        for (int i = 0; i < w * h; i++)
                            magArr[i] = MathF.Sqrt(pFX[i] * pFX[i] + pFY[i] * pFY[i]);
                    }
                }

                using var resultMat = ApplyVectorBlur(matCurr, flowX, flowY, magArr,
                    amount, samples, blurType);

                if (_outputBitmapCache == null || _outputBitmapCache.PixelSize != currentBitmap.PixelSize)
                {
                    _outputBitmapCache?.Dispose();
                    float dpiX, dpiY; dc.GetDpi(out dpiX, out dpiY);
                    var props = new BitmapProperties1(new PixelFormat(Format.B8G8R8A8_UNorm, AlphaMode.Premultiplied), dpiX, dpiY, BitmapOptions.None);
                    _outputBitmapCache = dc.CreateBitmap(currentBitmap.PixelSize, props);
                }
                unsafe
                {
                    int pitch = (int)resultMat.Step();
                    byte[] buf = new byte[pitch * h];
                    Marshal.Copy((nint)resultMat.DataPointer, buf, 0, buf.Length);
                    _outputBitmapCache.CopyFromMemory(buf, pitch);
                }
                resultImage = _outputBitmapCache;
            }

            this.wrap.TransformMatrix = Matrix3x2.CreateTranslation(offset);
            this.wrap.SetInput(0, resultImage, true);
            return effectDescription.DrawDescription;
        }

        /// <summary>
        /// 各タイプに応じたベクターブラー処理:
        /// - Natural: 勾配方向にブラー、長さは勾配の大きさに比例（二乗カーブ）
        /// - ConstantLength: 勾配方向にブラー、長さは一定（Amount）
        /// - Perpendicular: 勾配に垂直な方向にブラー、長さは勾配の大きさに比例
        /// - DirectionCenter: マップ明るさを角度に変換、中心から前後にブラー
        /// - DirectionFading: 同上だが前方のみ
        /// </summary>
        private unsafe Mat ApplyVectorBlur(Mat src, Mat flowX, Mat flowY, float[] magArr,
            double amount, int samples, AqueousBlurEffect.BlurType blurType)
        {
            int w = src.Cols, h = src.Rows;
            var result = new Mat(h, w, src.Type());

            byte* pSrc = (byte*)src.DataPointer;
            byte* pDst = (byte*)result.DataPointer;
            float* pFX = (float*)flowX.DataPointer;
            float* pFY = (float*)flowY.DataPointer;
            int srcStep = (int)src.Step();
            int dstStep = (int)result.Step();

            const float SENSITIVITY = 4.0f;
            float amountAbs = (float)Math.Abs(amount);
            float sign = amount >= 0 ? 1.0f : -1.0f;
            float halfLen0 = amountAbs * 2.0f;
            int maxHalfSamples = Math.Max(1, samples / 2);

            bool isNatural = (blurType == AqueousBlurEffect.BlurType.Natural);
            bool isPerpendicular = (blurType == AqueousBlurEffect.BlurType.Perpendicular);
            bool isConstant = (blurType == AqueousBlurEffect.BlurType.ConstantLength);
            bool isDirectionCenter = (blurType == AqueousBlurEffect.BlurType.DirectionCenter);
            bool isDirectionFading = (blurType == AqueousBlurEffect.BlurType.DirectionFading);
            bool isDirection = isDirectionCenter || isDirectionFading;

            byte* srcP = pSrc; byte* dstP = pDst;
            float* fxP = pFX; float* fyP = pFY;
            int srcS = srcStep; int dstS = dstStep;

            Parallel.For(0, h, y =>
            {
                for (int x = 0; x < w; x++)
                {
                    byte* pOut = dstP + y * dstS + x * 4;
                    float mag = magArr[y * w + x];
                    float vx = fxP[y * w + x];
                    float vy = fyP[y * w + x];

                    if (mag < 1e-6f && !isDirection)
                    {
                        byte* pPx = srcP + y * srcS + x * 4;
                        pOut[0] = pPx[0]; pOut[1] = pPx[1];
                        pOut[2] = pPx[2]; pOut[3] = pPx[3];
                        continue;
                    }

                    // --- ブラー長の決定 ---
                    float halfLen;
                    if (isDirection)
                    {
                        // Direction系: 常に一定長
                        halfLen = halfLen0;
                    }
                    else if (isConstant)
                    {
                        // Constant Length: 勾配があれば一定長
                        halfLen = halfLen0;
                    }
                    else
                    {
                        // Natural / Perpendicular: 二乗カーブで勾配に応じた長さ
                        float raw = mag * SENSITIVITY;
                        float magFactor = raw * raw;
                        if (magFactor > 1.0f) magFactor = 1.0f;
                        halfLen = magFactor * halfLen0;
                    }

                    if (halfLen < 0.5f)
                    {
                        byte* pPx = srcP + y * srcS + x * 4;
                        pOut[0] = pPx[0]; pOut[1] = pPx[1];
                        pOut[2] = pPx[2]; pOut[3] = pPx[3];
                        continue;
                    }

                    // --- 方向の正規化 ---
                    float dirX, dirY;
                    if (isDirection)
                    {
                        // Direction系: vx,vy は既に単位ベクトル
                        dirX = vx * sign;
                        dirY = vy * sign;
                    }
                    else
                    {
                        float invMag = 1.0f / mag;
                        dirX = vx * invMag * sign;
                        dirY = vy * invMag * sign;
                    }

                    int halfSamples = Math.Min(maxHalfSamples, Math.Max(1, (int)halfLen));
                    float stepSize = halfLen / halfSamples;

                    // --- サンプリング ---
                    float sumB = 0, sumG = 0, sumR = 0, sumA = 0;
                    int count = 0;

                    // 中心ピクセル
                    {
                        byte* p = srcP + y * srcS + x * 4;
                        sumB += p[0]; sumG += p[1]; sumR += p[2]; sumA += p[3];
                        count++;
                    }

                    if (isDirectionFading)
                    {
                        // Direction Fading: 前方のみ
                        for (int s = 1; s <= halfSamples * 2; s++)
                        {
                            float t = s * stepSize;
                            float sx = x + dirX * t;
                            float sy = y + dirY * t;
                            if (sx < 0 || sx >= w - 1 || sy < 0 || sy >= h - 1) break;

                            SampleBilinear(srcP, srcS, w, h, sx, sy,
                                out float sB, out float sG, out float sR, out float sA);
                            sumB += sB; sumG += sG; sumR += sR; sumA += sA;
                            count++;
                        }
                    }
                    else
                    {
                        // Natural / ConstantLength / Perpendicular / DirectionCenter:
                        // 前方+後方の対称ブラー
                        for (int dir = -1; dir <= 1; dir += 2)
                        {
                            for (int s = 1; s <= halfSamples; s++)
                            {
                                float t = s * stepSize * dir;
                                float sx = x + dirX * t;
                                float sy = y + dirY * t;
                                if (sx < 0 || sx >= w - 1 || sy < 0 || sy >= h - 1) break;

                                SampleBilinear(srcP, srcS, w, h, sx, sy,
                                    out float sB, out float sG, out float sR, out float sA);
                                sumB += sB; sumG += sG; sumR += sR; sumA += sA;
                                count++;
                            }
                        }
                    }

                    if (count > 0)
                    {
                        float inv = 1.0f / count;
                        pOut[0] = ClampByte(sumB * inv);
                        pOut[1] = ClampByte(sumG * inv);
                        pOut[2] = ClampByte(sumR * inv);
                        pOut[3] = ClampByte(sumA * inv);
                    }
                    else
                    {
                        byte* p = srcP + y * srcS + x * 4;
                        pOut[0] = p[0]; pOut[1] = p[1]; pOut[2] = p[2]; pOut[3] = p[3];
                    }
                }
            });

            return result;
        }

        private static unsafe void SampleBilinear(byte* pSrc, int srcStep, int w, int h,
            float fx, float fy, out float b, out float g, out float r, out float a)
        {
            int x0 = (int)fx, y0 = (int)fy;
            int x1 = x0 + 1, y1 = y0 + 1;
            if (x0 < 0) x0 = 0; if (x1 >= w) x1 = w - 1;
            if (y0 < 0) y0 = 0; if (y1 >= h) y1 = h - 1;
            float dx = fx - x0, dy = fy - y0;
            if (dx < 0) dx = 0; if (dy < 0) dy = 0;
            float w00 = (1 - dx) * (1 - dy), w10 = dx * (1 - dy), w01 = (1 - dx) * dy, w11 = dx * dy;
            byte* p00 = pSrc + y0 * srcStep + x0 * 4;
            byte* p10 = pSrc + y0 * srcStep + x1 * 4;
            byte* p01 = pSrc + y1 * srcStep + x0 * 4;
            byte* p11 = pSrc + y1 * srcStep + x1 * 4;
            b = p00[0] * w00 + p10[0] * w10 + p01[0] * w01 + p11[0] * w11;
            g = p00[1] * w00 + p10[1] * w10 + p01[1] * w01 + p11[1] * w11;
            r = p00[2] * w00 + p10[2] * w10 + p01[2] * w01 + p11[2] * w11;
            a = p00[3] * w00 + p10[3] * w10 + p01[3] * w01 + p11[3] * w11;
        }

        private static byte ClampByte(float v) =>
            v < 0 ? (byte)0 : v > 255 ? (byte)255 : (byte)(v + 0.5f);

        private ID2D1Bitmap1? LoadWicBitmap(ID2D1DeviceContext dc, string path)
        {
            if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path)) return null;
            try
            {
                using var factory = new Vortice.WIC.IWICImagingFactory();
                using var decoder = factory.CreateDecoderFromFileName(path, null, System.IO.FileAccess.Read, Vortice.WIC.DecodeOptions.CacheOnLoad);
                using var frame = decoder.GetFrame(0);
                using var converter = factory.CreateFormatConverter();
                converter.Initialize(frame, Vortice.WIC.PixelFormat.Format32bppPBGRA, Vortice.WIC.BitmapDitherType.None, null, 0.0, Vortice.WIC.BitmapPaletteType.MedianCut);
                var props = new BitmapProperties1(new PixelFormat(Format.B8G8R8A8_UNorm, AlphaMode.Premultiplied), 96f, 96f, BitmapOptions.None);
                return dc.CreateBitmapFromWicBitmap(converter, props);
            }
            catch { return null; }
        }

        private ID2D1Bitmap1 ConvertToBitmap(ID2D1DeviceContext dc, ID2D1Image image, out Vector2 offset)
        {
            var localBounds = dc.GetImageLocalBounds(image);
            offset = new Vector2(localBounds.Left, localBounds.Top);
            int w = (int)Math.Ceiling(localBounds.Right - localBounds.Left);
            int h = (int)Math.Ceiling(localBounds.Bottom - localBounds.Top);
            if (w <= 0) w = 1; if (h <= 0) h = 1;
            float dpiX, dpiY; dc.GetDpi(out dpiX, out dpiY);
            var bmpProps = new BitmapProperties1(new PixelFormat(Format.B8G8R8A8_UNorm, AlphaMode.Premultiplied), dpiX, dpiY, BitmapOptions.Target);
            var bmp = dc.CreateBitmap(new SizeI(w, h), bmpProps);
            var oldTarget = dc.Target;
            dc.Target = bmp;
            dc.BeginDraw();
            dc.Clear(new Color4(0, 0, 0, 0));
            dc.DrawImage(image, new Vector2(-localBounds.Left, -localBounds.Top));
            dc.EndDraw();
            dc.Target = oldTarget;
            return bmp;
        }

        private Mat BitmapToOpenCvMat(ID2D1DeviceContext dc, ID2D1Bitmap bitmap)
        {
            ID2D1Bitmap1? readableBitmap = null;
            var bmp1 = bitmap.QueryInterfaceOrNull<ID2D1Bitmap1>();
            if (bmp1 != null && (bmp1.Options & BitmapOptions.CpuRead) != BitmapOptions.None)
                readableBitmap = bmp1;
            else
            {
                bmp1?.Dispose();
                var props = new BitmapProperties1(new PixelFormat(Format.B8G8R8A8_UNorm, AlphaMode.Premultiplied), 96f, 96f, BitmapOptions.CannotDraw | BitmapOptions.CpuRead);
                readableBitmap = dc.CreateBitmap(bitmap.PixelSize, props);
                readableBitmap.CopyFromBitmap(bitmap);
            }
            var map = readableBitmap.Map(MapOptions.Read);
            try
            {
                using var tempMat = new Mat(readableBitmap.PixelSize.Height, readableBitmap.PixelSize.Width, MatType.CV_8UC4, map.Bits, map.Pitch);
                return tempMat.Clone();
            }
            finally
            {
                readableBitmap.Unmap();
                if (readableBitmap != bitmap) readableBitmap.Dispose();
            }
        }

        public void ClearInput() => this.wrap.SetInput(0, null, true);
        public void SetInput(ID2D1Image? input) => this.inputCurrent = input;

        public void Dispose()
        {
            this.disposer.Dispose();
            _grayCurr.Dispose();
            _videoCapture?.Dispose();
            _externalMapCache?.Dispose();
            _outputBitmapCache?.Dispose();
        }
    }
}