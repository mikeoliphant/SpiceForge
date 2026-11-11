using System;
using System.Configuration;
using System.Drawing;
using System.Globalization;
using UILayout;

namespace SpiceForgePlugin
{
    /// <summary>An image drawn rotated about its center, and scaled (unlike UILayout's RotatingImageElement).</summary>
    public class ScaledRotatingImageElement : UIElement
    {
        public UIImage Image { get; }
        public float Scale { get; }
        public UIColor Color { get; set; } = UIColor.White;
        public float Rotation { get; set; }

        public ScaledRotatingImageElement(string imageName, float scale)
        {
            Image = Layout.Current.GetImage(imageName);
            Scale = scale;
        }

        protected override void GetContentSize(out float width, out float height)
        {
            width = Image.Width * Scale;
            height = Image.Height * Scale;
        }

        protected override void DrawContents()
        {
            base.DrawContents();

            Layout.Current.GraphicsContext.DrawImage(Image, ContentBounds.CenterX, ContentBounds.CenterY, Color, Rotation,
                new System.Numerics.Vector2(Image.Width / 2.0f, Image.Height / 2.0f), Scale);
        }
    }

    public class ParameterDial : Dock
    {
        public double MinValue { get; set; }
        public double MaxValue { get; set; }
        public double DefaultValue { get; set; }
        public double RangePower { get; set; } = 1.0;
        public Action<double> ValueChangedAction { get; set; }
        public Action HoldAction { get; set; }

        // How much bigger than its images the dial is drawn.
        public const float ImageScale = 1.5f;

        ImageElement background;
        ScaledRotatingImageElement pointer;
        double currentValue;

        public ParameterDial()
        {
            MinValue = 0;
            MaxValue = 1;
            DefaultValue = 0.5;

            background = new ImageElement("DialBackground") { HorizontalAlignment = EHorizontalAlignment.Center, VerticalAlignment = EVerticalAlignment.Center };
            background.DesiredWidth = background.Image.Width * ImageScale;
            background.DesiredHeight = background.Image.Height * ImageScale;
            Children.Add(background);

            pointer = new ScaledRotatingImageElement("DialPointer", ImageScale) { HorizontalAlignment = EHorizontalAlignment.Center, VerticalAlignment = EVerticalAlignment.Center, Color = UIColor.Black };
            Children.Add(pointer);

            SetValue(DefaultValue);
        }

        public void SetDialColor(UIColor color)
        {
            background.Color = color;
        }

        public void SetPointerColor(UIColor color)
        {
            pointer.Color = color;
        }

        public void SetValue(double value)
        {
            currentValue = MathUtil.Clamp(value, MinValue, MaxValue);

            double val = (currentValue - MinValue) / (MaxValue - MinValue);

            double maxAngle = 143;

            double angle = -maxAngle + (val * maxAngle * 2);

            pointer.Rotation = MathUtil.ToRadians((float)angle);
        }

        double touchStartValue;

        public override bool HandleTouch(in Touch touch)
        {
            switch (touch.TouchState)
            {
                case ETouchState.Pressed:
                    CaptureTouch(touch);
                    touchStartValue = currentValue;
                    break;
                case ETouchState.Moved:
                case ETouchState.Held:
                    if (HaveTouchCapture)
                    {
                        double delta = TouchCaptureStartPosition.Y - touch.Position.Y;

                        double range = MaxValue - MinValue;

                        double newValue = touchStartValue + ((delta * range) / 160);    //(double)PixGame.Instance.ScreenPPI);

                        newValue = MathUtil.Clamp(newValue, MinValue, MaxValue);

                        SetValue(newValue);

                        if (ValueChangedAction != null)
                            ValueChangedAction(newValue);
                    }
                    break;
                case ETouchState.Released:
                case ETouchState.Invalid:
                    ReleaseTouch();
                    break;
                default:
                    break;
            }

            if (IsDoubleTap(touch, this))
            {
                SetValue(DefaultValue);

                if (ValueChangedAction != null)
                    ValueChangedAction(DefaultValue);
            }

            return true;
        }

        //public override bool HandleGesture(PixGesture gesture)
        //{
        //    if (gesture.GestureType == EPixGestureType.Hold)
        //    {
        //        if (HoldAction != null)
        //        {
        //            HoldAction();

        //            return true;
        //        }
        //    }

        //    return base.HandleGesture(gesture);
        //}
    }

    public class ParameterValueDisplay : NinePatchWrapper
    {
        public float HoldSeconds { get; set; }
        public float FadeSeconds { get; set; }
        public string ValueFormat { get; set; } = "0.0";

        float visibleSeconds = 0;
        StringBuilderTextBlock textBlock;
        double value = double.MinValue;

        public ParameterValueDisplay()
            : base(Layout.Current.GetImage("HoverTextOutline"))
        {
            Visible = false;
            Color = new UIColor(64, 64, 64);

            HorizontalAlignment = EHorizontalAlignment.Center;
            VerticalAlignment = EVerticalAlignment.Center;

            HoldSeconds = 0.25f;
            FadeSeconds = 0.25f;

            textBlock = new StringBuilderTextBlock
            {
                TextColor = UIColor.White,
                TextFont = Layout.Current.GetFont("SmallFont"),
                Margin = new LayoutPadding(5, 5),
                HorizontalAlignment = EHorizontalAlignment.Center,
                VerticalAlignment = EVerticalAlignment.Center
            };

            Child = textBlock;
        }

        public void SetValue(double value)
        {
            if (value != this.value)
            {
                this.value = value;

                textBlock.StringBuilder.Clear();
                textBlock.StringBuilder.AppendFormat(ValueFormat, value);
            }

            UpdateActive();
        }

        public void UpdateActive()
        {
            Visible = true;

            visibleSeconds = 0;

            Color = new UIColor((byte)Color.R, (byte)Color.G, (byte)Color.B, (byte)255);
        }

        protected override void DrawContents()
        {
            base.DrawContents();

            if (Visible)
            {
                visibleSeconds += Layout.Current.SecondsElapsed;

                if (visibleSeconds > HoldSeconds)
                {
                    if (visibleSeconds > (HoldSeconds + FadeSeconds))
                    {
                        Visible = false;
                    }
                    else
                    {
                        byte alpha = (byte)(255 * MathUtil.Saturate(1.0f - ((visibleSeconds - HoldSeconds) / FadeSeconds)));

                        Color = new UIColor((byte)Color.R, (byte)Color.G, (byte)Color.B, alpha);
                    }
                }
            }
        }
    }

    public class LevelBar : Dock
    {
        public bool DoLogDisplay { get; set; }
        public double WarnLevel { get; set; }
        public Func<double> GetValue { get; set; }

        bool isHorizontal = false;
        ImageElement activeLevelImage;
        double lastValue = 0;
        double clip = 0;

        public LevelBar()
            : this(isHorizontal: false)
        {
        }

        public LevelBar(bool isHorizontal)
        {
            this.isHorizontal = isHorizontal;

            WarnLevel = 0.8f;
            BackgroundColor = UIColor.Black;
            HorizontalAlignment = EHorizontalAlignment.Stretch;
            VerticalAlignment = EVerticalAlignment.Stretch;

            string imageName = isHorizontal ? "LevelDisplayHorizontal" : "LevelDisplay";

            Children.Add(new ImageElement(imageName)
            {
                Color = new UIColor(20, 20, 20),
            });

            activeLevelImage = new ImageElement(imageName)
            {
                HorizontalAlignment = isHorizontal ? EHorizontalAlignment.Left : EHorizontalAlignment.Stretch,
                VerticalAlignment = isHorizontal ? EVerticalAlignment.Stretch : EVerticalAlignment.Bottom,
                Color = UIColor.Green,
            };
            activeLevelImage.Visible = false;
            Children.Add(activeLevelImage);
        }

        public void SetValue(double value)
        {
            if (value < 0.01)
                value = 0;

            if (clip > 0)
            {
                clip -= 0.1;

                if (clip <= 0)
                    activeLevelImage.Color = UIColor.Green;
            }

            if (value >= 1.0)
            {
                clip = 1;
                activeLevelImage.Color = UIColor.Red;
            }
            else if (value >= WarnLevel)
            {
                clip = 1;
                activeLevelImage.Color = UIColor.Orange;
            }

            if (value != lastValue)
            {
                lastValue = value;

                double displayValue = DoLogDisplay ? Math.Min(Math.Log10((value * 9.0) + 1.0), 1.0) : value;

                if (isHorizontal)
                {
                    int width = (int)((double)activeLevelImage.Image.Width * displayValue);

                    if (width > activeLevelImage.Image.Width)
                        width = activeLevelImage.Image.Width;

                    activeLevelImage.SourceRectangle = new Rectangle(0, 0, width, activeLevelImage.Image.Height);
                    activeLevelImage.DesiredWidth = ContentBounds.Width * (float)displayValue;
                    activeLevelImage.Visible = (displayValue > 0);
                }
                else
                {
                    int height = (int)((double)activeLevelImage.Image.Height * displayValue);

                    if (height > activeLevelImage.Image.Height)
                        height = activeLevelImage.Image.Height;

                    activeLevelImage.SourceRectangle = new Rectangle(0, activeLevelImage.Image.Height - height, activeLevelImage.Image.Width, height);
                    activeLevelImage.DesiredHeight = ContentBounds.Height * (float)displayValue;
                    activeLevelImage.Visible = (displayValue > 0);
                }

                UpdateContentLayout();
            }
        }

        protected override void DrawContents()
        {
            if (GetValue != null)
            {
                SetValue(GetValue());
            }

            base.DrawContents();
        }
    }

    public class VerticalLevelBar : LevelBar
    {
        public VerticalLevelBar()
            : base(isHorizontal: false)
        {

        }
    }

    public class HorizontalLevelBar : LevelBar
    {
        public HorizontalLevelBar()
            : base(isHorizontal: true)
        {

        }
    }
}
