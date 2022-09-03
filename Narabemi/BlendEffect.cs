using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace Narabemi
{
    public class BlendEffect : ShaderEffect
    {
        public const string DefaultShaderFilePath = @"Shaders\blend.fxc";
        private const SamplingMode DefaultSamplingMode = SamplingMode.NearestNeighbor;

        public BlendEffect()
        {
            PixelShader = new();
        }

        private void LoadShader(string path)
        {
            if (string.IsNullOrEmpty(path))
                return;

            if (!File.Exists(path))
                throw new FileNotFoundException("shader file doesn't exist.", path);

            using (var ms = new MemoryStream(File.ReadAllBytes(path)))
                PixelShader.SetStreamSource(ms);

            UpdateShaderValue(Input0Property);
            UpdateShaderValue(Input1Property);
            UpdateShaderValue(WidthProperty);
            UpdateShaderValue(HeightProperty);
            UpdateShaderValue(RatioProperty);
            UpdateShaderValue(BorderWidthProperty);
        }

        public string ShaderPath
        {
            get => (string)GetValue(ShaderPathProperty);
            set => SetValue(ShaderPathProperty, value);
        }
        public static readonly DependencyProperty ShaderPathProperty =
            DependencyProperty.Register(nameof(ShaderPath), typeof(string), typeof(BlendEffect), new UIPropertyMetadata(DefaultShaderFilePath, ShaderPathChangedCallback));
        private static void ShaderPathChangedCallback(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is BlendEffect blendEffect)
                blendEffect.LoadShader((string)e.NewValue);
        }

        public Brush Input0
        {
            get => (Brush)GetValue(Input0Property);
            set => SetValue(Input0Property, value);
        }
        public static DependencyProperty Input0Property = RegisterPixelShaderSamplerProperty(nameof(Input0), typeof(BlendEffect), 0, DefaultSamplingMode);

        public Brush Input1
        {
            get => (Brush)GetValue(Input1Property);
            set => SetValue(Input1Property, value);
        }
        public static DependencyProperty Input1Property = RegisterPixelShaderSamplerProperty(nameof(Input1), typeof(BlendEffect), 1, DefaultSamplingMode);

        public double Width
        {
            get => (double)GetValue(WidthProperty);
            set => SetValue(WidthProperty, value);
        }
        public static readonly DependencyProperty WidthProperty =
            DependencyProperty.Register(nameof(Width), typeof(double), typeof(BlendEffect), new UIPropertyMetadata(0.5, PixelShaderConstantCallback(0)));

        public double Height
        {
            get => (double)GetValue(HeightProperty);
            set => SetValue(HeightProperty, value);
        }
        public static readonly DependencyProperty HeightProperty =
            DependencyProperty.Register(nameof(Height), typeof(double), typeof(BlendEffect), new UIPropertyMetadata(0.5, PixelShaderConstantCallback(1)));

        public double Ratio
        {
            get => (double)GetValue(RatioProperty);
            set => SetValue(RatioProperty, value);
        }
        public static readonly DependencyProperty RatioProperty = DependencyProperty.Register(
            nameof(Ratio), typeof(double), typeof(BlendEffect), new UIPropertyMetadata(0.5, PixelShaderConstantCallback(2)));

        public double BorderWidth
        {
            get => (double)GetValue(BorderWidthProperty);
            set => SetValue(BorderWidthProperty, value);
        }
        public static readonly DependencyProperty BorderWidthProperty =
            DependencyProperty.Register(nameof(BorderWidth), typeof(double), typeof(BlendEffect), new UIPropertyMetadata(0.5, PixelShaderConstantCallback(3)));

        public Color BorderColor
        {
            get => (Color)GetValue(BorderColorProperty);
            set => SetValue(BorderColorProperty, value);
        }
        public static readonly DependencyProperty BorderColorProperty =
            DependencyProperty.Register(nameof(BorderColor), typeof(Color), typeof(BlendEffect), new PropertyMetadata(Colors.White, PixelShaderConstantCallback(4)));
    }
}
