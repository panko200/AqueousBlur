using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Controls;
using YukkuriMovieMaker.Exo;
using YukkuriMovieMaker.Player.Video;
using YukkuriMovieMaker.Plugin.Effects;
using YukkuriMovieMaker.Settings;

namespace AqueousBlur
{
    [VideoEffect("アクアスブラー", ["加工"], ["aqueous", "blur"])]
    internal class AqueousBlurEffect : VideoEffectBase
    {
        public override string Label => "アクアスブラー";

        // --- Type ---
        [Display(GroupName = "基本", Name = "タイプ", Description = "ブラーの種類を選択します。")]
        [EnumComboBox]
        public BlurType Type { get => _blurType; set => Set(ref _blurType, value); }
        private BlurType _blurType = BlurType.Natural;

        [Display(GroupName = "基本", Name = "ぼかし量", Description = "ブラーの強度（長さ）を設定します。")]
        [AnimationSlider("F1", "", -500, 500)]
        public Animation Amount { get; } = new Animation(20.0, -500, 500);

        [Display(GroupName = "基本", Name = "品質", Description = "トレースするサンプル数。値を上げると滑らかになりますが重くなります。")]
        [AnimationSlider("F0", "回", 1, 128)]
        public Animation Samples { get; } = new Animation(32, 1, 128);

        // --- マップソース設定 ---
        [Display(GroupName = "マップ", Name = "マップ元", Description = "ベクトルを算出する画像を選びます。")]
        [EnumComboBox]
        public MapSourceType MapSource { get => _mapSource; set => Set(ref _mapSource, value); }
        private MapSourceType _mapSource = MapSourceType.CurrentFrame;

        [Display(GroupName = "マップ", Name = "外部ファイル", Description = "マップ元が「外部ファイル」の時に使用される画像や動画を選択します。")]
        [FileSelector(FileGroupType.VideoItem)]
        public string MapFilePath { get => _mapFilePath; set => Set(ref _mapFilePath, value); }
        private string _mapFilePath = string.Empty;

        // --- AE特有のパラメータ群 ---
        [Display(GroupName = "マップ", Name = "角度", Description = "ベクトルの方向を回転させます。\nデフォルト(0°)で等高線方向(エッジの境界線に沿う方向)に流れます。")]
        [AnimationSlider("F1", "°", -180, 180)]
        public Animation AngleOffset { get; } = new Animation(0, -180, 180);

        [Display(GroupName = "マップ", Name = "マップの柔らかさ", Description = "ベクトル検出前の画像の滑らかさ。ノイズを抑えて大きな流れを作ります。")]
        [AnimationSlider("F1", "", 0, 100)]
        public Animation MapSoftness { get; } = new Animation(20.0, 0, 100);

        [Display(GroupName = "マップ", Name = "リッジの滑らかさ", Description = "ベクトル場(流れ)自体の滑らかさ。上げると線のガタつきが消え、美しい流線になります。")]
        [AnimationSlider("F2", "", 0, 50)]
        public Animation RidgeSmoothness { get; } = new Animation(1.0, 0, 50);

        public enum BlurType
        {
            [Display(Name = "Natural")]
            Natural,
            [Display(Name = "Constant Length")]
            ConstantLength,
            [Display(Name = "Perpendicular")]
            Perpendicular,
            [Display(Name = "Direction Center")]
            DirectionCenter,
            [Display(Name = "Direction Fading")]
            DirectionFading
        }

        public enum MapSourceType
        {
            [Display(Name = "None (現在の映像)")]
            CurrentFrame,
            [Display(Name = "外部ファイル")]
            ExternalFile
        }

        public override IEnumerable<string> CreateExoVideoFilters(int keyFrameIndex, ExoOutputDescription exoOutputDescription)
        {
            return [];
        }

        public override IVideoEffectProcessor CreateVideoEffect(IGraphicsDevicesAndContext devices)
        {
            return new AqueousBlurEffectProcessor(devices, this);
        }

        protected override IEnumerable<IAnimatable> GetAnimatables() => [Amount, Samples, AngleOffset, MapSoftness, RidgeSmoothness];
    }
}