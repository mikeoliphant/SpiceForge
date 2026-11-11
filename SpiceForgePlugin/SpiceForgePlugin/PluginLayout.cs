using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Forms;
using SpiceForge;
using UILayout;
using Button = UILayout.Button;
using Menu = UILayout.Menu;
using MenuItem = UILayout.MenuItem;

namespace SpiceForgePlugin
{
    /// <summary>
    /// The plugin's editor, laid out like LiveSPICE's VST: the schematic and its file buttons, the engine settings, and
    /// then a dial for every pot and a button for every switch, labelled with their names.
    /// </summary>
    public class SpiceForgeLayout : MonoGameLayout
    {
        // Controls per row before wrapping to the next.
        const int ControlsPerRow = 8;

        SpiceForgePlugin plugin;
        Dock mainDock;
        UIElementWrapper controlWrapper;
        TextBlock schematicText;
        TextToggleButton autoReloadButton;
        TextButton oversampleButton;
        TextButton iterationsButton;
        UIColor foregroundColor = UIColor.White;

        // The plugin version the controls were last built for.
        int shownVersion = -1;

        public SpiceForgeLayout(SpiceForgePlugin plugin)
        {
            this.plugin = plugin;
        }

        public override void SetHost(MonoGameHost host)
        {
            base.SetHost(host);

            Host.InactiveSleepTime = TimeSpan.Zero;

            Host.Window.Title = "SpiceForge";

            var loader = new MonoGameContentLoader(Host.Content);

            LoadImageManifest(loader, "ImageManifest.xml");

            GraphicsContext.SingleWhitePixelImage = GetImage("SingleWhitePixel");
            GraphicsContext.SamplerState = new Microsoft.Xna.Framework.Graphics.SamplerState()
            {
                AddressU = Microsoft.Xna.Framework.Graphics.TextureAddressMode.Clamp,
                AddressV = Microsoft.Xna.Framework.Graphics.TextureAddressMode.Clamp,
                Filter = Microsoft.Xna.Framework.Graphics.TextureFilter.Anisotropic,
                MipMapLevelOfDetailBias = -0.8f
            };

            DefaultFont = GetFont("MainFont");
            DefaultFont.SpriteFont.Spacing = 1;

            GetFont("SmallFont").SpriteFont.Spacing = 0;

            DefaultForegroundColor = UIColor.White;

            DefaultOutlineNinePatch = GetImage("PopupBackground");

            DefaultPressedNinePatch = GetImage("ButtonPressed");
            DefaultUnpressedNinePatch = GetImage("ButtonUnpressed");

            DefaultDragImage = GetImage("ButtonPressed");

            RootUIElement = mainDock = new Dock()
            {
                BackgroundColor = UIColor.Black,
                Padding = new LayoutPadding(20)
            };

            VerticalStack vStack = new VerticalStack()
            {
                HorizontalAlignment = EHorizontalAlignment.Stretch,
                VerticalAlignment = EVerticalAlignment.Stretch,
                ChildSpacing = 10
            };
            mainDock.Children.Add(vStack);

            // The schematic, and buttons to load, edit and reload it.
            HorizontalStack schematicStack = new HorizontalStack()
            {
                HorizontalAlignment = EHorizontalAlignment.Right,
                DesiredHeight = 80
            };
            vStack.Children.Add(schematicStack);

            schematicText = new TextBlock()
            {
                Margin = new LayoutPadding(10),
                VerticalAlignment = EVerticalAlignment.Center
            };
            schematicStack.Children.Add(schematicText);

            schematicStack.Children.Add(new ImageButton("FileOpen")
            {
                VerticalAlignment = EVerticalAlignment.Stretch,
                ClickAction = LoadSchematic
            });

            schematicStack.Children.Add(new ImageButton("FileEdit")
            {
                VerticalAlignment = EVerticalAlignment.Stretch,
                ClickAction = plugin.EditSchematic
            });

            schematicStack.Children.Add(new ImageButton("Reload")
            {
                VerticalAlignment = EVerticalAlignment.Stretch,
                ClickAction = plugin.ReloadSchematic
            });

            // Engine settings: auto reload, oversampling and the iteration cap.
            HorizontalStack settingsStack = new HorizontalStack()
            {
                HorizontalAlignment = EHorizontalAlignment.Center,
                ChildSpacing = 10
            };
            vStack.Children.Add(settingsStack);

            autoReloadButton = new TextToggleButton("Auto Reload: On", "Auto Reload: Off")
            {
                VerticalAlignment = EVerticalAlignment.Center
            };
            autoReloadButton.SetPressed(plugin.AutoReload);
            autoReloadButton.PressAction = delegate { plugin.SetAutoReload(autoReloadButton.IsPressed); };
            settingsStack.Children.Add(autoReloadButton);

            settingsStack.Children.Add(new TextBlock("Oversample:")
            {
                Margin = new LayoutPadding(20, 0, 0, 0),
                VerticalAlignment = EVerticalAlignment.Center
            });
            oversampleButton = new TextButton(plugin.Oversample.ToString()) { VerticalAlignment = EVerticalAlignment.Center };
            oversampleButton.ClickAction = delegate
            {
                ShowChoices(oversampleButton, SpiceForgePlugin.OversampleChoices, plugin.SetOversample);
            };
            settingsStack.Children.Add(oversampleButton);

            settingsStack.Children.Add(new TextBlock("Iterations:")
            {
                Margin = new LayoutPadding(20, 0, 0, 0),
                VerticalAlignment = EVerticalAlignment.Center
            });
            iterationsButton = new TextButton(plugin.Iterations.ToString()) { VerticalAlignment = EVerticalAlignment.Center };
            iterationsButton.ClickAction = delegate
            {
                ShowChoices(iterationsButton, SpiceForgePlugin.IterationChoices, plugin.SetIterations);
            };
            settingsStack.Children.Add(iterationsButton);

            // The circuit's controls, or what the plugin is doing instead.
            controlWrapper = new UIElementWrapper()
            {
                HorizontalAlignment = EHorizontalAlignment.Stretch,
                VerticalAlignment = EVerticalAlignment.Stretch
            };
            vStack.Children.Add(controlWrapper);

            UpdateParameters();
        }

        public override void Update(float secondsElapsed)
        {
            base.Update(secondsElapsed);

            // A new processor or status (a build finished, or failed) rebuilds the controls.
            if (plugin.Version != shownVersion)
                UpdateParameters();
        }

        // A popup menu of numeric choices under a button; choosing one shows it on the button and passes it on.
        void ShowChoices(TextButton button, int[] choices, Action<int> choose)
        {
            List<MenuItem> items = new List<MenuItem>();

            foreach (int choice in choices)
            {
                items.Add(new ContextMenuItem
                {
                    Text = choice.ToString(),
                    SelectAction = delegate
                    {
                        button.Text = choice.ToString();
                        button.UpdateContentLayout();
                        choose(choice);
                    }
                });
            }

            ShowPopup(new Menu(items), button.ContentBounds.Center);
        }

        void UpdateParameters()
        {
            shownVersion = plugin.Version;

            SchematicProcessor processor = plugin.CurrentProcessor;

            schematicText.Text = String.IsNullOrEmpty(plugin.SchematicPath) ? "No Schematic Loaded" : Path.GetFileNameWithoutExtension(plugin.SchematicPath);
            oversampleButton.Text = plugin.Oversample.ToString();
            iterationsButton.Text = plugin.Iterations.ToString();
            autoReloadButton.SetPressed(plugin.AutoReload);

            if (processor == null || plugin.Status != null)
            {
                controlWrapper.Child = new TextBlock(plugin.Status ?? (String.IsNullOrEmpty(plugin.SchematicPath) ? "Load a schematic (.schx) to run audio through it." : ""))
                {
                    HorizontalAlignment = EHorizontalAlignment.Center,
                    VerticalAlignment = EVerticalAlignment.Center,
                    TextColor = foregroundColor
                };

                // A failure leaves the last circuit's controls useless; a build in progress leaves them working, so
                // keep showing them under the status.
                if (processor != null && plugin.Status != null && plugin.Status.StartsWith("Building"))
                    controlWrapper.Child = WithStatus(AddControls(processor), plugin.Status);
            }
            else
            {
                controlWrapper.Child = AddControls(processor);
            }

            mainDock.UpdateContentLayout();
        }

        UIElement WithStatus(UIElement controls, string status)
        {
            VerticalStack stack = new VerticalStack()
            {
                HorizontalAlignment = EHorizontalAlignment.Stretch,
                VerticalAlignment = EVerticalAlignment.Stretch,
                ChildSpacing = 10
            };
            stack.Children.Add(new TextBlock(status)
            {
                HorizontalAlignment = EHorizontalAlignment.Center,
                TextColor = foregroundColor,
                TextFont = GetFont("SmallFont")
            });
            stack.Children.Add(controls);

            return stack;
        }

        // The circuit's pots and switches in a box titled with its name, in rows.
        UIElement AddControls(SchematicProcessor processor)
        {
            NinePatchWrapper outline = new NinePatchWrapper(GetImage("PluginBackground"))
            {
                Color = new UIColor(32, 32, 32),
                HorizontalAlignment = EHorizontalAlignment.Center,
                VerticalAlignment = EVerticalAlignment.Center,
                Padding = new LayoutPadding(30)
            };

            VerticalStack verticalStack = new VerticalStack()
            {
                HorizontalAlignment = EHorizontalAlignment.Stretch,
                VerticalAlignment = EVerticalAlignment.Stretch,
                ChildSpacing = 10
            };
            outline.Child = verticalStack;

            verticalStack.Children.Add(new TextBlock(processor.Name)
            {
                HorizontalAlignment = EHorizontalAlignment.Center,
                TextColor = foregroundColor
            });

            List<UIElement> controls = new List<UIElement>();

            for (int i = 0; i < processor.Pots.Count; i++)
                controls.Add(AddPot(processor.Pots[i]));

            for (int i = 0; i < processor.Switches.Count; i++)
                controls.Add(AddSwitch(processor.Switches[i]));

            if (controls.Count == 0)
            {
                verticalStack.Children.Add(new TextBlock("(no pots or switches)")
                {
                    HorizontalAlignment = EHorizontalAlignment.Center,
                    TextColor = foregroundColor,
                    TextFont = GetFont("SmallFont")
                });
            }

            HorizontalStack row = null;

            for (int i = 0; i < controls.Count; i++)
            {
                if (i % ControlsPerRow == 0)
                {
                    row = new HorizontalStack()
                    {
                        HorizontalAlignment = EHorizontalAlignment.Center,
                        ChildSpacing = 10
                    };
                    verticalStack.Children.Add(row);
                }

                row.Children.Add(controls[i]);
            }

            return outline;
        }

        VerticalStack ControlStack(string name)
        {
            VerticalStack controlVStack = new VerticalStack()
            {
                HorizontalAlignment = EHorizontalAlignment.Stretch,
                VerticalAlignment = EVerticalAlignment.Stretch
            };

            controlVStack.Children.Add(new TextBlock(name)
            {
                Margin = new LayoutPadding(5, 0),
                HorizontalAlignment = EHorizontalAlignment.Center,
                TextColor = foregroundColor,
                TextFont = GetFont("SmallFont")
            });

            return controlVStack;
        }

        // A pot: a dial from 0 to 1 (double-click for the schematic's setting), showing its value while it moves.
        UIElement AddPot(PotControl pot)
        {
            VerticalStack controlVStack = ControlStack(pot.Name);

            Dock controlDock = new Dock() { HorizontalAlignment = EHorizontalAlignment.Stretch, VerticalAlignment = EVerticalAlignment.Center };
            controlVStack.Children.Add(controlDock);

            ParameterValueDisplay valueDisplay = new ParameterValueDisplay()
            {
                ValueFormat = "{0:0.00}"
            };

            ParameterDial dial = new ParameterDial()
            {
                MinValue = 0,
                MaxValue = 1,
                DefaultValue = pot.DefaultPosition
            };

            dial.SetDialColor(new UIColor(80, 80, 80));
            dial.SetPointerColor(UIColor.White);
            dial.SetValue(pot.Position);

            dial.ValueChangedAction = delegate (double value)
            {
                pot.Position = value;

                valueDisplay.SetValue(value);
            };

            controlDock.Children.Add(dial);
            controlDock.Children.Add(valueDisplay);

            return controlVStack;
        }

        // A switch: on/off as a lamp to click, more positions as a button that pops up a menu of them.
        UIElement AddSwitch(SwitchControl sw)
        {
            VerticalStack controlVStack = ControlStack(sw.Name);

            if (sw.Positions == 2)
            {
                Button lamp = new Button()
                {
                    IsToggleButton = true,
                    HorizontalAlignment = EHorizontalAlignment.Center,
                    VerticalAlignment = EVerticalAlignment.Center,
                    PressedElement = LampImage("LampOn"),
                    UnpressedElement = LampImage("LampOff")
                };
                lamp.SetPressed(sw.Position == 1);
                lamp.PressAction = delegate { sw.Position = lamp.IsPressed ? 1 : 0; };

                // Centered in a dial-sized space, so it lines up with the pots in its row.
                UIImage dialImage = GetImage("DialBackground");
                Dock slot = new Dock()
                {
                    HorizontalAlignment = EHorizontalAlignment.Center,
                    DesiredWidth = dialImage.Width * ParameterDial.ImageScale,
                    DesiredHeight = dialImage.Height * ParameterDial.ImageScale
                };
                slot.Children.Add(lamp);

                controlVStack.Children.Add(slot);
            }
            else
            {
                TextButton button = new TextButton(sw.Position.ToString())
                {
                    HorizontalAlignment = EHorizontalAlignment.Center,
                    VerticalAlignment = EVerticalAlignment.Center,
                    Margin = new LayoutPadding(5)
                };

                int[] positions = new int[sw.Positions];
                for (int i = 0; i < positions.Length; i++)
                    positions[i] = i;

                button.ClickAction = delegate
                {
                    ShowChoices(button, positions, position => sw.Position = position);
                };

                controlVStack.Children.Add(button);
            }

            return controlVStack;
        }

        // A lamp image, at the dials' scale.
        ImageElement LampImage(string name)
        {
            ImageElement image = new ImageElement(name)
            {
                HorizontalAlignment = EHorizontalAlignment.Center,
                VerticalAlignment = EVerticalAlignment.Center
            };
            image.DesiredWidth = image.Image.Width * ParameterDial.ImageScale;
            image.DesiredHeight = image.Image.Height * ParameterDial.ImageScale;

            return image;
        }

        void LoadSchematic()
        {
            OpenFileDialog openFileDialog = new OpenFileDialog();

            openFileDialog.Filter = "LiveSPICE Schematics|*.schx";

            if (openFileDialog.ShowDialog() == DialogResult.OK)
            {
                if (!string.IsNullOrEmpty(openFileDialog.FileName))
                {
                    plugin.LoadSchematic(openFileDialog.FileName);
                }
            }
        }
    }
}
