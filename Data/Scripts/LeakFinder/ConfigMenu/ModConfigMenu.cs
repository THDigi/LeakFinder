using System;
using VRageMath;
using static Draygo.API.HudAPIv2;
using static Draygo.API.HudAPIv2.MenuRootCategory;

namespace Digi.LeakFinder.ConfigMenu
{
    public class ModConfigMenu
    {
        public void CreateMenu()
        {
            Log.Info($"TextAPI detected, creating menu.");

            var cfg = LeakFinderMod.Instance.ClientConfig;

            var rootCategory = new MenuRootCategory("Leak Finder", MenuFlag.PlayerMenu, "Leak Finder Settings");

            new ItemColor(rootCategory, "Particle Color World",
                () => cfg.ParticleWorld, (v) => cfg.ParticleWorld = v, ClientConfig.ParticleWorldDefault, ClientConfig.UseAlpha);

            new ItemColor(rootCategory, "Particle Color Overlay",
                () => cfg.ParticleOverlay, (v) => cfg.ParticleOverlay = v, ClientConfig.ParticleOverlayDefault, ClientConfig.UseAlpha);
        }
    }

    public class ItemColor
    {
        public readonly Func<Color> ValueGetter;
        public readonly Action<Color> ValueSetter;

        public string Title;

        MenuColorPickerInput Item;

        public ItemColor(MenuCategoryBase category, string title, Func<Color> getter, Action<Color> setter, Color defaultValue, bool useAlpha = false)
        {
            ValueGetter = getter;
            ValueSetter = setter;

            Title = title;

            string inputDialogTitle = $"{Title} | Default: {defaultValue.R},{defaultValue.G},{defaultValue.B}";

            Item = new MenuColorPickerInput(title, category, getter.Invoke(), inputDialogTitle, OnSubmit, OnSlide, OnCancel, useAlpha);

            UpdateTitle();
        }

        public void UpdateTitle()
        {
            Color v = ValueGetter.Invoke();
            Item.Text = $"{Title}: {v.R},{v.G},{v.B} <color={v.R},{v.G},{v.B}>□";
        }

        void SaveValue(Color color)
        {
            ValueSetter.Invoke(color);
            LeakFinderMod.Instance.ScheduleSaveConfig();
        }

        void OnSubmit(Color color)
        {
            try
            {
                SaveValue(color);
                Item.InitialColor = color;
                UpdateTitle();
            }
            catch(Exception e)
            {
                Log.Error(e);
            }
        }

        void OnSlide(Color color)
        {
            try
            {
                SaveValue(color);
                UpdateTitle();
            }
            catch(Exception e)
            {
                Log.Error(e);
            }
        }

        void OnCancel()
        {
            try
            {
                SaveValue(Item.InitialColor);
                UpdateTitle();
            }
            catch(Exception e)
            {
                Log.Error(e);
            }
        }
    }
}
