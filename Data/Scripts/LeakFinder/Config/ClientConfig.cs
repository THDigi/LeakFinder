using System;
using System.IO;
using Sandbox.ModAPI;
using VRage.Game.ModAPI.Ingame.Utilities;
using VRageMath;

namespace Digi.LeakFinder.ConfigMenu
{
    public class ClientConfig
    {
        // Do not rename, these are also the names in the ini
        public Color ParticleWorld = ParticleWorldDefault;
        public Color ParticleOverlay = ParticleOverlayDefault;

        public static readonly Color ParticleWorldDefault = new Color(0, 120, 255);
        public static readonly Color ParticleOverlayDefault = new Color(140, 140, 0);

        const string FileName = "Config.ini";
        const string IniSection = "Config";

        readonly char[] ColorSplit = new char[] { ',' };
        public static readonly bool UseAlpha = false;

        public ClientConfig()
        {
        }

        void LoadConfig(MyIni iniParser)
        {
            ReadColor(iniParser, nameof(ParticleWorld), ref ParticleWorld);
            ReadColor(iniParser, nameof(ParticleOverlay), ref ParticleOverlay);
        }

        void SaveConfig(MyIni iniParser)
        {
            SaveColor(iniParser, nameof(ParticleWorld), ParticleWorld, " obscured by geometry");
            SaveColor(iniParser, nameof(ParticleOverlay), ParticleOverlay, " visible through walls");
        }

        public void FirstLoad()
        {
            Load();
            Save();
        }

        public void Load()
        {
            try
            {
                if(!MyAPIGateway.Utilities.FileExistsInLocalStorage(FileName, typeof(ClientConfig)))
                    return;

                using(TextReader reader = MyAPIGateway.Utilities.ReadFileInLocalStorage(FileName, typeof(ClientConfig)))
                {
                    string text = reader.ReadToEnd();

                    MyIni iniParser = new MyIni();

                    MyIniParseResult result;
                    if(iniParser.TryParse(text, out result))
                    {
                        Log.Info($"Loading {FileName} with settings:\n{iniParser.ToString()}");
                        LoadConfig(iniParser);
                    }
                    else
                        Log.Error($"Error parsing {FileName}: {result.ToString()}");
                }
            }
            catch(Exception e)
            {
                Log.Error(e);
            }
        }

        public void Save()
        {
            try
            {
                MyIni iniParser = new MyIni();
                SaveConfig(iniParser);
                string saveText = iniParser.ToString();

                using(TextWriter writer = MyAPIGateway.Utilities.WriteFileInLocalStorage(FileName, typeof(ClientConfig)))
                {
                    writer.Write(saveText);
                }

                //Log.Info($"Saved {FileName} with settings:\n{saveText}");
            }
            catch(Exception e)
            {
                Log.Error(e);
            }
        }

        void SaveColor(MyIni iniParser, string name, Color color, string comment = null)
        {
            string colorString = UseAlpha ? $"{color.R}, {color.G}, {color.B}, {color.A}" : $"{color.R}, {color.G}, {color.B}";
            iniParser.Set(IniSection, name, colorString);

            if(comment != null)
                iniParser.SetComment(IniSection, name, comment);
        }

        bool ReadColor(MyIni iniParser, string name, ref Color color)
        {
            string value = iniParser.Get(IniSection, name).ToString();

            byte r, g, b, a;
            string[] split = value.Split(ColorSplit);

            if(split.Length >= 3
            && byte.TryParse(split[0], out r)
            && byte.TryParse(split[1], out g)
            && byte.TryParse(split[2], out b))
            {
                a = 255; // default alpha if not defined
                if(split.Length == 3 || (UseAlpha && byte.TryParse(split[3], out a)))
                {
                    color = new Color(r, g, b, a);
                    return true;
                }
            }

            Log.Error($"{FileName} has invalid color for {name}; expected R,G,B or R,G,B,A with values from 0 to 255");
            return false;
        }
    }
}
