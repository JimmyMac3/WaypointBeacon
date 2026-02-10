#nullable disable

using Cairo;
using System;
using System.Reflection.Emit;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.GameContent;
using static System.Net.Mime.MediaTypeNames;

namespace WaypointBeacon
{
    // Beacon Manager Settings Dialog
	
    public class GuiDialogBeaconManagerSettings : GuiDialog
    {
        private readonly ICoreClientAPI api;
        private readonly WaypointBeaconModSystem mod;
        private readonly Action onClosed;
        private readonly Action onOpened;

        // Configurable render distance limits
        private int minBeaconRenderDist;
        private int maxBeaconRenderDist;
        private const int SwitchSizePx = 28;

        public GuiDialogBeaconManagerSettings(ICoreClientAPI api, WaypointBeaconModSystem mod, Action onClosed = null, Action onOpened = null) : base(api)
        {
            this.api = api;
            this.mod = mod;
            this.onClosed = onClosed;
            this.onOpened = onOpened;
            // Load max distance from mod config
            this.minBeaconRenderDist = mod?.MinRenderDistance ?? 250;
            this.maxBeaconRenderDist = mod?.MaxRenderDistance ?? 1000;
            if (this.maxBeaconRenderDist < this.minBeaconRenderDist) this.maxBeaconRenderDist = this.minBeaconRenderDist;
        }

        public override string ToggleKeyCombinationCode => null;

        public override bool TryOpen()
        {
            try
            {
                Compose();
                return base.TryOpen();
            }
            catch (Exception e)
            {
                api?.Logger?.Error("[WaypointBeacon] Beacon Manager TryOpen failed: {0}", e);
                return false;
            }
        }

        public override void OnGuiOpened()
        {
            base.OnGuiOpened();
            onOpened?.Invoke();
        }

        public override void OnGuiClosed()
        {
            base.OnGuiClosed();
            onClosed?.Invoke();
        }

        private void OnClose() => TryClose();

        private void Compose()
        {
            const int width = 600;
            const int height = 550;

            ElementBounds dialogBounds = ElementBounds.Fixed(0, 0, width, height)
                .WithAlignment(EnumDialogArea.CenterMiddle);

            ElementBounds bgBounds = ElementBounds.Fill.WithFixedPadding(0);

            SingleComposer?.Dispose();
            SingleComposer = api.Gui.CreateCompo("waypointbeacon-beaconmanager", dialogBounds)
                .AddDialogBG(bgBounds, true)
                .AddDialogTitleBar("Beacon Manager (K)", OnClose);

            // Layout
            int pad = 18;
            int y = 54;
            int rowH = 28;
            int rowGap = 18;
            int ctrlW = 245;
            int ctrlX = width - (ctrlW + pad);
            int labelW = ctrlX - pad * 2;

            CairoFont labelFont = CairoFont.WhiteSmallishText();

            SingleComposer.AddStaticText("                  The Waypoint Beacon Mod. Version 1.0\nYour map knows. Your eyes can too - 3D beacons for waypoints",
                labelFont, ElementBounds.Fixed(14, y, width - pad, rowH * 2), "Welcome-lbl");
           
            y += rowH * 2 + rowGap;

            AddSliderRow("Label Font Size", "bm-labelfont", labelFont, pad, labelW, ctrlX, ctrlW, ref y, rowH, rowGap, OnLabelFontSizeChanged, 300,
                "Make it BIG so you can read it from orbit Or tiny so it whispers politely.\n\nDefault=80");
            AddDropDownRow("Label Style", "bm-labelstyle", labelFont, pad, labelW, ctrlX, ctrlW, ref y, rowH, rowGap,
                new[] { "Label Only", "Label + Distance", "Label + Coords" }, mod?.LabelStyleMode ?? 0, 300,
                "Label Only: Just the name and icon. Clean. Classic. Unapologetically minimal\n\nLabel + Distance: Adds, how far did I walk for this? Great for cardio guilt.\n\nLabel + Coords: Adds [x, y, z] so you can math your way into happiness.\n\nDefault=Distance");
            AddDropDownRow("Show Labels", "bm-showlabels", labelFont, pad, labelW, ctrlX, ctrlW, ref y, rowH, rowGap,
                new[] { "Always", "Never", "Auto Hide" }, mod?.ShowLabelsMode ?? 0, 300,
                "Always: All labels, all the time. Maximum information. Maximum clutter. No regrets.\n\nNever: Shhh. Labels are forbidden. Your eyes may finally rest.\n\nAuto Hide: Only shows the label youre aiming at. Like a spotlight for your crosshair.\n\nDefault=Auto Hide");
            AddSliderRow("Max Render Distance", "bm-maxrender", labelFont, pad, labelW, ctrlX, ctrlW, ref y, rowH, rowGap, OnMaxRenderDistanceChanged, 300,
                "Beacon-Vision.\nCrank it up for superhero sight\nOr turn it down and save your eyeballs.\nMax can be changed in config.\n\nDefault=1000 blocks.");
            AddSwitchRow("Near Beacon Fade-out", "bm-fadenear", labelFont, ctrlX, pad, labelW, ref y, ctrlW, rowH, rowGap, OnNearFadeChanged, 300,
                "Beacons get shy the closer you get.\nFade distance can be changed in config.\n\nDefault=On.");
            AddSwitchRow("New Waypoint = Beacon", "bm-newwp", labelFont, ctrlX, pad, labelW, ref y, ctrlW, rowH, rowGap, OnNewWaypointBeaconChanged, 300,
                "Turn waypoints into glorious sky lasers.\nOff=They stay shy and normal.\n(Auto Map Markers friendly)\n\nDefault=On.");
            AddSwitchRow("Hide All Beacons", "bm-hideall", labelFont, ctrlX, pad, labelW, ref y, ctrlW, rowH, rowGap, OnHideAllBeaconsChanged, 200,
                "Panic button! Nuke it all!\nThis just temporarily stops the mod from rendering stuff.\n\nDefault=Off");
            AddSwitchRow("Show Beams", "bm-showbeams", labelFont, ctrlX, pad, labelW, ref y, ctrlW, rowH, rowGap, OnShowBeamsChanged, 220,
                "Sky lasers on/off.\nOff=Labels only.\nOn=Full I live here now mode.\n\nDefault=On");

            // Bottom buttons
            int btnW = 170;
            int leftBtnX = (width - (btnW * 2 + rowGap)) / 2;
            int rightBtnX = leftBtnX + btnW + rowGap;
            int leftBtnY = y;

            SingleComposer
                .AddSmallButton("Enable ALL Beacons", OnEnableAllBeacons, ElementBounds.Fixed(leftBtnX, leftBtnY, btnW, rowH), EnumButtonStyle.Normal, "bm-enableall")
                .AddSmallButton("Disable ALL Beacons", OnDisableAllBeacons, ElementBounds.Fixed(rightBtnX, leftBtnY, btnW, rowH), EnumButtonStyle.Normal, "bm-disableall");

            SingleComposer.Compose(false);
            y += rowH + rowGap;
            // Sync UI from current settings
            RefreshValues();
        }

        private void RefreshValues()
        {
            if (SingleComposer == null) return;

            // Max Render Distance: use actual block range instead of 0-100
            int blocks = mod?.MaxRenderDistanceXZ ?? minBeaconRenderDist;
            try { SingleComposer.GetSlider("bm-maxrender").SetValues(blocks, minBeaconRenderDist, maxBeaconRenderDist, 10, " Blocks"); } catch { }

            // Label Font Size slider (0..100)
            int labelFontSize = mod?.LabelFontSizeSlider ?? 100;
            try { SingleComposer.GetSlider("bm-labelfont").SetValues(labelFontSize, 0, 100, 1, ""); } catch { }

            // Hide All Beacons switch
            bool hideAll = !(mod?.GlobalBeaconsEnabled ?? true);
            try { SingleComposer.GetSwitch("bm-hideall").SetValue(hideAll); } catch { }

            // New Waypoint Beacon switch
            bool newWpBeacon = mod?.DefaultNewWaypointBeaconOn ?? false;
            try { SingleComposer.GetSwitch("bm-newwp").SetValue(newWpBeacon); } catch { }

            // Show Beams switch
            bool showBeams = mod?.BeamsEnabled ?? true;
            try { SingleComposer.GetSwitch("bm-showbeams").SetValue(showBeams); } catch { }


            // Near Beacon Fade-out switch
            bool nearFade = mod?.NearBeaconFadeOutEnabled ?? false;
            try { SingleComposer.GetSwitch("bm-fadenear").SetValue(nearFade); } catch { }

            // (Icons are always shown)
        }

        // Event handlers
        private bool OnMaxRenderDistanceChanged(int sliderValue)
        {
            // sliderValue is the actual block distance (blocks), not 0-100
            mod?.SetMaxRenderDistanceXZ(sliderValue);
            return true;
        }

        private bool OnLabelFontSizeChanged(int sliderValue)
        {
            mod?.SetLabelFontSizeSlider(sliderValue);
            return true;
        }
        private void OnNearFadeChanged(bool on)
        {
            mod?.SetNearBeaconFadeOutEnabled(on);
        }

        private void OnNewWaypointBeaconChanged(bool on)
        {
            mod?.SetDefaultNewWaypointBeaconOn(on);
        }

        private void OnHideAllBeaconsChanged(bool hideAll)
        {
            mod?.SetGlobalBeaconsEnabled(!hideAll);
        }

        private void OnShowBeamsChanged(bool on)
        {
            mod?.SetBeamsEnabled(on);
        }

        private bool OnEnableAllBeacons()
        {
            try { mod?.SetAllWaypointBeacons(true); } catch { }
            return true;
        }

        private bool OnDisableAllBeacons()
        {
            try { mod?.SetAllWaypointBeacons(false); } catch { }
            return true;
        }

        // Range conversion helpers
        private int SliderToBlocks(int sliderValue)
        {
            sliderValue = Math.Max(0, Math.Min(sliderValue, 100));
            double t = sliderValue / 100.0;
            int blocks = (int)Math.Round(minBeaconRenderDist + (maxBeaconRenderDist - minBeaconRenderDist) * t);
            blocks = (int)(Math.Round(blocks / 10.0) * 10);
            return Math.Max(minBeaconRenderDist, Math.Min(blocks, maxBeaconRenderDist));
        }

        private int BlocksToSlider(int blocks)
        {
            blocks = Math.Max(minBeaconRenderDist, Math.Min(blocks, maxBeaconRenderDist));
            double t = (blocks - minBeaconRenderDist) / (double)(maxBeaconRenderDist - minBeaconRenderDist);
            int slider = (int)Math.Round(t * 100.0);
            return Math.Max(0, Math.Min(slider, 100));
        }

        // UI builder methods
        private void AddSwitchRow(string label, string key, CairoFont font,
            int swX, int labelX, int labelW, ref int y, int swSize, int rowH, int rowGap, Action<bool> onChanged, int tipW, string tooltip = null)
        {
            var extents = font.GetFontExtents();
            double textHeight = extents.Height;
            double rowCenter = y + rowH / 2.0;
            double labelY = rowCenter - textHeight / 2.0;

            double swY = rowCenter - SwitchSizePx / 2.0;

            SingleComposer.AddSwitch(onChanged, ElementBounds.Fixed(swX, swY, SwitchSizePx, SwitchSizePx), key);
            SingleComposer.AddStaticText(label, font, ElementBounds.Fixed(labelX, labelY + 2, labelW, textHeight), key + "-lbl");

            if (!string.IsNullOrEmpty(tooltip))
            {
                ElementBounds tipBounds = ElementBounds.Fixed(labelX, y, 250, rowH);
                SingleComposer.AddHoverText(tooltip, CairoFont.WhiteSmallText(), tipW, tipBounds, key + "-tip");
            }

            y += rowH + rowGap;
        }

        private void AddSliderRow(string label, string key, CairoFont font,
            int labelX, int labelW, int ctrlX, int ctrlW, ref int y, int rowH, int rowGap, ActionConsumable<int> onChanged, int tipW, string tooltip = null)
        {
            var extents = font.GetFontExtents();
            double textHeight = extents.Height;
            double rowCenter = y + rowH / 2.0;
            double labelY = rowCenter - textHeight / 2.0;

            SingleComposer.AddStaticText(label, font, ElementBounds.Fixed(labelX, labelY + 2, labelW, rowH), key + "-lbl");
            SingleComposer.AddSlider(onChanged, ElementBounds.Fixed(ctrlX, labelY, ctrlW, rowH), key);

            if (!string.IsNullOrEmpty(tooltip))
            {
                ElementBounds tipBounds = ElementBounds.Fixed(labelX, y, 250, rowH);
                SingleComposer.AddHoverText(tooltip, CairoFont.WhiteSmallText(), tipW, tipBounds, key + "-tip");
            }

            y += rowH + rowGap;
        }

        private void AddDropDownRow(string label, string key, CairoFont font,
            int pad, int labelW, int ctrlX, int ctrlW, ref int y, int rowH, int rowGap,
            string[] options, int selectedIndex, int tipW, string tooltip = null)
        {
            var extents = font.GetFontExtents();
            double textHeight = extents.Height;
            double rowCenter = y + rowH / 2.0;
            double labelY = rowCenter - textHeight / 2.0;
            double ctrlY = rowCenter - rowH / 2.0;

            SingleComposer.AddStaticText(label, font, ElementBounds.Fixed(pad, labelY, labelW, textHeight), key + "-lbl");
            SingleComposer.AddDropDown(options, options, selectedIndex, (code, selected) =>
            {
                if (!selected) return;

                int selectedIdx = 0;
                for (int j = 0; j < options.Length; j++)
                {
                    if (options[j] == code) { selectedIdx = j; break; }
                }

                if (key == "bm-showlabels")
                {
                    mod?.SetShowLabelsMode(selectedIdx);
                }
                else if (key == "bm-labelstyle")
                {
                    mod?.SetLabelStyleMode(selectedIdx);
                }
            },
                ElementBounds.Fixed(ctrlX, ctrlY, ctrlW, rowH), font, key);

            if (!string.IsNullOrEmpty(tooltip))
            {
                ElementBounds tipBounds = ElementBounds.Fixed(pad, y, 250, rowH);
                SingleComposer.AddHoverText(tooltip, CairoFont.WhiteSmallText(), tipW, tipBounds, key + "-tip");
            }

            y += rowH + rowGap;
        }
    }
}
