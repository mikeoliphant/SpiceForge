using System;
using System.Collections.Generic;
using System.IO;
using AudioPlugSharpUILayout;
using SpiceForge;
using UILayout;

namespace SpiceForgePlugin
{
    /// <summary>
    /// The plugin's editor, laid out like LiveSPICE's VST: the schematic and its file buttons, the engine settings, and
    /// then a knob for every pot and a switch for every switch, labelled with their names. It's built from UILayout's
    /// themed controls and AudioPlugSharp's parameter controls, bound to the plugin's pot and switch parameters.
    /// </summary>
    public class SpiceForgeEditor
    {
        // Controls per row before wrapping to the next.
        const int ControlsPerRow = 8;

        readonly SpiceForgePlugin plugin;
        readonly UITheme theme;
        readonly VerticalStack root;
        readonly TextBlock schematicText;
        readonly TextToggleButton autoReloadButton;
        readonly DropDown<int> oversampleDropDown;
        readonly DropDown<int> iterationsDropDown;
        readonly DropDown<bool> resamplingDropDown;
        readonly UIElementWrapper controlWrapper;

        // The circuit's controls shown, which follow their parameters (host automation) each frame.
        readonly List<ParameterControl> parameterControls = new List<ParameterControl>();

        // The plugin version the controls were last built for.
        int shownVersion = -1;

        public SpiceForgeEditor(SpiceForgePlugin plugin)
        {
            this.plugin = plugin;

            theme = Layout.Current.Theme;

            root = new VerticalStack()
            {
                HorizontalAlignment = EHorizontalAlignment.Stretch,
                VerticalAlignment = EVerticalAlignment.Stretch,
                BackgroundColor = theme.Background,
                Padding = new LayoutPadding(16),
                ChildSpacing = 12
            };

            // The schematic, and buttons to load, edit and reload it.
            Dock schematicDock = new Dock()
            {
                HorizontalAlignment = EHorizontalAlignment.Stretch,
                VerticalAlignment = EVerticalAlignment.Top
            };
            root.Children.Add(schematicDock);

            schematicText = new TextBlock()
            {
                HorizontalAlignment = EHorizontalAlignment.Left,
                VerticalAlignment = EVerticalAlignment.Center
            };
            schematicDock.Children.Add(schematicText);

            HorizontalStack fileButtons = new HorizontalStack()
            {
                HorizontalAlignment = EHorizontalAlignment.Right,
                VerticalAlignment = EVerticalAlignment.Center,
                ChildSpacing = 8
            };
            schematicDock.Children.Add(fileButtons);

            fileButtons.Children.Add(new TextButton("Open...", LoadSchematic));
            fileButtons.Children.Add(new TextButton("Edit", plugin.EditSchematic));
            fileButtons.Children.Add(new TextButton("Reload", plugin.ReloadSchematic));

            // Engine settings: auto reload, oversampling, the iteration cap and the resampling.
            HorizontalStack settingsStack = new HorizontalStack()
            {
                HorizontalAlignment = EHorizontalAlignment.Center,
                ChildSpacing = 8
            };
            root.Children.Add(settingsStack);

            // Pressed while on.
            autoReloadButton = new TextToggleButton("Auto Reload", "Auto Reload")
            {
                VerticalAlignment = EVerticalAlignment.Center
            };
            autoReloadButton.PressAction = delegate { plugin.SetAutoReload(autoReloadButton.IsPressed); };
            settingsStack.Children.Add(autoReloadButton);

            oversampleDropDown = NumberDropDown(SpiceForgePlugin.OversampleChoices);
            oversampleDropDown.SelectAction = delegate { plugin.SetOversample(oversampleDropDown.Value); };
            AddSetting(settingsStack, "Oversample", oversampleDropDown);

            iterationsDropDown = NumberDropDown(SpiceForgePlugin.IterationChoices);
            iterationsDropDown.SelectAction = delegate { plugin.SetIterations(iterationsDropDown.Value); };
            AddSetting(settingsStack, "Iterations", iterationsDropDown);

            resamplingDropDown = new DropDown<bool>()
            {
                VerticalAlignment = EVerticalAlignment.Center
            };
            resamplingDropDown.SetItems(new[] { ("Sinc", true), ("Linear", false) });
            resamplingDropDown.SelectAction = delegate { plugin.SetSincResampling(resamplingDropDown.Value); };
            AddSetting(settingsStack, "Resampling", resamplingDropDown);

            // The circuit's controls, or what the plugin is doing instead.
            controlWrapper = new UIElementWrapper()
            {
                HorizontalAlignment = EHorizontalAlignment.Stretch,
                VerticalAlignment = EVerticalAlignment.Stretch
            };
            root.Children.Add(controlWrapper);

            UpdateControls();
        }

        /// <summary>The editor's root element.</summary>
        public UIElement Root
        {
            get { return root; }
        }

        /// <summary>Call each frame: rebuilds the controls when the plugin has a new circuit or status, and has them
        /// follow their parameters.</summary>
        public void Update()
        {
            // A new processor or status (a build finished, or failed) rebuilds the controls.
            if (plugin.Version != shownVersion)
                UpdateControls();

            foreach (ParameterControl control in parameterControls)
                control.Update();
        }

        static DropDown<int> NumberDropDown(int[] choices)
        {
            DropDown<int> dropDown = new DropDown<int>()
            {
                VerticalAlignment = EVerticalAlignment.Center
            };

            List<(string, int)> items = new List<(string, int)>();

            foreach (int choice in choices)
                items.Add((choice.ToString(), choice));

            dropDown.SetItems(items);

            return dropDown;
        }

        void AddSetting(HorizontalStack stack, string name, UIElement control)
        {
            stack.Children.Add(new TextBlock(name)
            {
                Margin = new LayoutPadding(8, 0, 0, 0),
                VerticalAlignment = EVerticalAlignment.Center,
                TextColor = theme.MutedText
            });

            stack.Children.Add(control);
        }

        void UpdateControls()
        {
            shownVersion = plugin.Version;

            SchematicProcessor processor = plugin.CurrentProcessor;

            schematicText.Text = String.IsNullOrEmpty(plugin.SchematicPath) ? "No Schematic Loaded" : Path.GetFileNameWithoutExtension(plugin.SchematicPath);
            autoReloadButton.SetPressed(plugin.AutoReload);
            oversampleDropDown.SelectValue(value => value == plugin.Oversample);
            iterationsDropDown.SelectValue(value => value == plugin.Iterations);
            resamplingDropDown.SelectValue(value => value == plugin.SincResampling);

            parameterControls.Clear();

            if ((processor == null) || (plugin.Status != null))
            {
                controlWrapper.Child = new TextBlock(plugin.Status ?? (String.IsNullOrEmpty(plugin.SchematicPath) ? "Load a schematic (.schx) to run audio through it." : ""))
                {
                    HorizontalAlignment = EHorizontalAlignment.Center,
                    VerticalAlignment = EVerticalAlignment.Center
                };

                // A failure leaves the last circuit's controls useless; a build in progress leaves them working, so
                // keep showing them under the status.
                if ((processor != null) && (plugin.Status != null) && plugin.Status.StartsWith("Building"))
                    controlWrapper.Child = WithStatus(AddControls(processor), plugin.Status);
            }
            else
            {
                controlWrapper.Child = AddControls(processor);
            }

            root.UpdateContentLayout();
        }

        UIElement WithStatus(UIElement controls, string status)
        {
            // The controls stay centered where they are without a status (a rebuild doesn't move them), with the status
            // at the bottom of their area.
            Dock dock = new Dock();

            dock.Children.Add(controls);

            dock.Children.Add(new TextBlock(status)
            {
                HorizontalAlignment = EHorizontalAlignment.Center,
                VerticalAlignment = EVerticalAlignment.Bottom,
                TextColor = theme.MutedText,
                TextFont = theme.SmallFont
            });

            return dock;
        }

        // The circuit's pots and switches in a section titled with its name, in rows.
        UIElement AddControls(SchematicProcessor processor)
        {
            VerticalStack verticalStack = new VerticalStack()
            {
                HorizontalAlignment = EHorizontalAlignment.Stretch,
                ChildSpacing = 12
            };

            verticalStack.Children.Add(new TextBlock(processor.Name)
            {
                HorizontalAlignment = EHorizontalAlignment.Center
            });

            List<UIElement> controls = new List<UIElement>();

            for (int i = 0; i < Math.Min(processor.Pots.Count, SpiceForgePlugin.PotCount); i++)
                controls.Add(AddPot(processor.Pots[i], i));

            for (int i = 0; i < Math.Min(processor.Switches.Count, SpiceForgePlugin.SwitchCount); i++)
                controls.Add(AddSwitch(processor.Switches[i], i));

            if (controls.Count == 0)
                verticalStack.Children.Add(Note("(no pots or switches)"));

            HorizontalStack row = null;

            for (int i = 0; i < controls.Count; i++)
            {
                if ((i % ControlsPerRow) == 0)
                {
                    row = new HorizontalStack()
                    {
                        HorizontalAlignment = EHorizontalAlignment.Center,
                        ChildSpacing = 16
                    };
                    verticalStack.Children.Add(row);
                }

                row.Children.Add(controls[i]);
            }

            int hiddenPots = Math.Max(0, processor.Pots.Count - SpiceForgePlugin.PotCount);
            int hiddenSwitches = Math.Max(0, processor.Switches.Count - SpiceForgePlugin.SwitchCount);

            if ((hiddenPots > 0) || (hiddenSwitches > 0))
            {
                verticalStack.Children.Add(Note($"Not shown, past the plugin's {SpiceForgePlugin.PotCount} pots and {SpiceForgePlugin.SwitchCount} switches: " +
                    $"{hiddenPots} pot(s), {hiddenSwitches} switch(es), left at their positions."));
            }

            return new Section(verticalStack)
            {
                HorizontalAlignment = EHorizontalAlignment.Center,
                VerticalAlignment = EVerticalAlignment.Center
            };
        }

        TextBlock Note(string text)
        {
            return new TextBlock(text)
            {
                HorizontalAlignment = EHorizontalAlignment.Center,
                TextColor = theme.MutedText,
                TextFont = theme.SmallFont
            };
        }

        // A pot: a knob for its parameter, labelled with the pot's name (double-click for the schematic's setting).
        UIElement AddPot(PotControl pot, int index)
        {
            ParameterControl control = ParameterControl.Knob(plugin.PotParameters[index]);

            control.Control.DefaultValue = pot.DefaultPosition;

            return Labelled(pot.Name, control);
        }

        // A switch: on/off as a lamp, more positions as a drop-down of them.
        UIElement AddSwitch(SwitchControl sw, int index)
        {
            SwitchPositionParameter parameter = plugin.SwitchParameters[index];
            ParameterControl control;

            if (sw.Positions == 2)
            {
                control = ParameterControl.Switch(parameter);
            }
            else
            {
                string[] names = new string[sw.Positions];

                for (int i = 0; i < names.Length; i++)
                    names[i] = i.ToString();

                control = new ParameterControl(parameter, new ChoiceDropDown(names))
                {
                    ShowValuePopup = false,
                    MinControlHeight = Layout.Current.GetImage(AudioTextures.KnobBody).LogicalHeight
                };

                // The parameter has no steps of its own (see SwitchPositionParameter): the drop-down's are the switch's.
                control.Control.StepCount = sw.Positions - 1;
                control.Control.Value = parameter.NormalizedEditValue;
            }

            control.Control.DefaultValue = SwitchPositionParameter.ToValue(sw.DefaultPosition, sw.Positions);

            return Labelled(sw.Name, control);
        }

        // A parameter control under the circuit control's name: the parameter's own name ("Pot 1") is the host's.
        UIElement Labelled(string name, ParameterControl control)
        {
            parameterControls.Add(control);

            control.ShowName = false;

            VerticalStack stack = new VerticalStack()
            {
                HorizontalAlignment = EHorizontalAlignment.Center,
                VerticalAlignment = EVerticalAlignment.Top,
                ChildSpacing = 4
            };

            stack.Children.Add(new TextBlock(name)
            {
                HorizontalAlignment = EHorizontalAlignment.Center,
                TextColor = theme.MutedText,
                TextFont = theme.SmallFont ?? theme.Font
            });

            stack.Children.Add(control);

            return stack;
        }

        void LoadSchematic()
        {
            string folder = String.IsNullOrEmpty(plugin.SchematicPath)
                ? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
                : Path.GetDirectoryName(plugin.SchematicPath);

            string path = Layout.Current.GetFile(folder, "LiveSPICE Schematics", "schx");

            if (!String.IsNullOrEmpty(path))
                plugin.LoadSchematic(path);
        }
    }
}
