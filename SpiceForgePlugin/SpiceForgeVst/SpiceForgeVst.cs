using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.Loader;
using System.Threading;
using AudioPlugSharp;
using UILayout;

namespace SpiceForgeVst
{
    public class SpiceForgeVst : AudioPluginBase
    {
        public string PluginFilePath;

        DoubleAudioIOPort stereoInput;
        DoubleAudioIOPort stereoOutput;

        double[][] inBuf = new double[2][];
        double[][] outBuf = new double[2][];

        MonoGameHost GameHost;

        public SpiceForgeVst()
		{
			Company = "Mike Oliphant";
			Website = "github.com/mikeoliphant";
			Contact = "contact@nostatic.org";
			PluginName = "SpiceForge";
			PluginCategory = "Fx";
            PluginVersion = System.Diagnostics.FileVersionInfo.GetVersionInfo(this.GetType().Assembly.Location).FileVersion;

            PluginID = 0x47B5882C49394EAD;

			HasUserInterface = true;
			EditorWidth = 800;
			EditorHeight = 400;
		}

        public override void Initialize()
        {
            base.Initialize();

            //Logger.Log("Plugin has " + plugin.GetNumInputs() + " inputs and " + plugin.GetNumOutputs() + " outputs");

            InputPorts = new AudioIOPort[] { stereoInput = new DoubleAudioIOPort("Stereo Input", EAudioChannelConfiguration.Stereo) };
            OutputPorts = new AudioIOPort[] { stereoOutput = new DoubleAudioIOPort("Stereo Output", EAudioChannelConfiguration.Stereo) };
        }

        public void LoadPlugin(string path)
        {
            PluginFilePath = path;
        }

        IntPtr parentWindow;

        public override void ShowEditor(IntPtr parentWindow)
        {
            Logger.Log("Show Editor");

            this.parentWindow = parentWindow;

            if (parentWindow == IntPtr.Zero)
            {
                RunGame();
            }
            else
            {
                Thread thread = new Thread(new ThreadStart(RunGame));

                thread.SetApartmentState(ApartmentState.STA);

                thread.Start();
            }
        }

        void RunGame()
        {
            Logger.Log("Start Host");

            try
            {
                int screenWidth = (int)EditorWidth;
                int screenHeight = (int)EditorHeight;

                SpiceForgeLayout layout = new SpiceForgeLayout(this);

                layout.Scale = 0.35f;

                using (GameHost = new MonoGameHost(parentWindow, screenWidth, screenHeight, fullscreen: false))
                {
                    GameHost.IsMouseVisible = true;

                    GameHost.StartGame(layout);
                }

                layout = null;
            }
            catch (Exception ex)
            {
                Logger.Log("Host run failed with: " + ex.ToString());
            }
        }

        public override void ResizeEditor(uint newWidth, uint newHeight)
        {
            base.ResizeEditor(newWidth, newHeight);

            if (GameHost != null)
            {
                GameHost.RequestResize((int)newWidth, (int)newHeight);
            }
        }


        public override void HideEditor()
        {
            base.HideEditor();

            GameHost.Exit();
        }

        public override void InitializeProcessing()
        {
            base.InitializeProcessing();

        }

        public override void SetMaxAudioBufferSize(uint maxSamples, EAudioBitsPerSample bitsPerSample)
        {
            base.SetMaxAudioBufferSize(maxSamples, bitsPerSample);

            inBuf[0] = new double[maxSamples];
            inBuf[1] = new double[maxSamples];

            outBuf[0] = new double[maxSamples];
            outBuf[1] = new double[maxSamples];
        }

        public override void Process()
        {
            base.Process();

            Host.ProcessAllEvents();

        }
    }
}
