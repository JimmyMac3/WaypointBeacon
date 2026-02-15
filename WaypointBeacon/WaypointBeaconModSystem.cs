#nullable disable

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;
using ProtoBuf;
using Vintagestory.API.Server;
using Vintagestory.API.Datastructures;

namespace WaypointBeacon
{
    using ProtoBuf;
    using Vintagestory.API.Server;

    [ProtoContract]
    public class WbSetPinnedPacket
    {
        [ProtoMember(1)] public int WaypointId { get; set; }
        [ProtoMember(2)] public bool Pinned { get; set; }

        // Fallback identity if WaypointId is your hash-id (common in your setup)
        [ProtoMember(3)] public double X { get; set; }
        [ProtoMember(4)] public double Y { get; set; }
        [ProtoMember(5)] public double Z { get; set; }
        [ProtoMember(6)] public string Title { get; set; }
    }


    [ProtoContract]
    public class WbRequestPinsPacket
    {
        // empty - client asks server for saved pin overrides
    }

    [ProtoContract]
    public class WbPinsSyncPacket
    {
        // Parallel lists for protobuf simplicity
        [ProtoMember(1)] public List<string> Keys { get; set; } = new List<string>();
        [ProtoMember(2)] public List<bool> Pinned { get; set; } = new List<bool>();
    }

    [ProtoContract]
    public class WbCreateWaypointPacket
    {
        [ProtoMember(1)] public int X { get; set; }
        [ProtoMember(2)] public int Y { get; set; }
        [ProtoMember(3)] public int Z { get; set; }
        [ProtoMember(4)] public string Title { get; set; }
        [ProtoMember(5)] public string Icon { get; set; }
        [ProtoMember(6)] public string Color { get; set; }
    }


    public class WaypointBeaconModSystem : ModSystem
    {
        private ICoreClientAPI capi;


        // Beacon Manager dialog (settings UI)
        private GuiDialogBeaconManagerSettings beaconManagerDialog;
        private bool beaconManagerIsOpen;
        private long tickListenerId;
        private long lastAddWaypointHotkeyTicks;
        private BeaconLabelRenderer labelRenderer;
        private BeaconBeamRenderer beamRenderer;

        private TextTextureUtil textUtil;
        private TextBackground textBg;

        private readonly List<BeaconInfo> visibleBeacons = new List<BeaconInfo>();

        // ---- General ----
        private const int DefaultMaxRenderDistanceXZ = 250;
        // Near-beacon fade tuning (in blocks). When enabled, beacons fade out as you approach.
        private const double DefaultNearFadeStartBlocks = 25.0;   // blocks
        private const double DefaultNearFadeEndBlocks = 10.0;     // blocks      // fully hidden when closer than this

        internal float ComputeNearFadeAlpha(Vec3d camPos, double bx, double by, double bz)
    {
        if (!NearBeaconFadeOutEnabled) return 1f;

        // Use config values (with safe defaults)
        double start = clientConfig?.NearFadeStartBlocks ?? DefaultNearFadeStartBlocks;
        double end = clientConfig?.NearFadeEndBlocks ?? DefaultNearFadeEndBlocks;

        // Sanity
        if (start < 1) start = DefaultNearFadeStartBlocks;
        if (end < 0) end = DefaultNearFadeEndBlocks;
        if (end >= start) end = start * 0.4;

        double dx = camPos.X - bx;
        double dy = camPos.Y - by;
        double dz = camPos.Z - bz;

        double dist = Math.Sqrt(dx * dx + dy * dy + dz * dz);

        if (dist <= end) return 0f;
        if (dist >= start) return 1f;

        double t = (dist - end) / (start - end);
        return (float)GameMath.Clamp(t, 0, 1);
    }
        // XZ-only (blocks)
        private const int MaxMaxRenderDistanceXZ = 1000;            // safety cap (blocks)
        private const float BeamRingRadius = 0.10f;
        private const int BeamRingLines = 10;

        // ---- Labels ----
        private const float LabelFontPx = 20f;
        private const int LabelMaxWidthPx = 4096;
        private const float LabelScreenYOffsetPx = 10f;
        private const int OutlinePx = 2;

        private const int ClampMarginPx = 18;

        // Anchor near base
        private const float LabelWorldYOffsetBlocks = 2.0f; // Y + 2

        private static bool SwapRedBlue = false;

        public override bool ShouldLoad(EnumAppSide forSide) => true;
        private IClientNetworkChannel clientChannel;
        private IServerNetworkChannel serverChannel;


        // Beacon toggle persistence (server stores per-player; client keeps local overrides)
        private readonly Dictionary<string, bool> beaconOverrides = new Dictionary<string, bool>();

        private bool requestedPins;

        // Track waypoints we've seen this session so we can apply the default beacon setting only to newly created waypoints
        private readonly HashSet<string> seenWaypointKeys = new HashSet<string>();
        private bool seenWaypointKeysInitialized;


        private const string PinsAttrKeyPrefix = "waypointbeacon:pins:";

        // ---- Client config (local only) ----
        private const string ClientConfigFileName = "waypointbeacon-client.json";

        public class WaypointBeaconClientConfig
        {
            public bool GlobalBeaconsEnabled = true;

            // Render beacon beams (vertical light columns)
            public bool BeamsEnabled = true;

            // Fade out beacon beams/labels when the player is very close
            public bool NearBeaconFadeOut = true;


            // Show waypoint icons next to beacon labels
            public bool ShowIconsInLabels = true;


            // Label visibility: 0=Always, 1=Never, 2=AutoHide
            public int ShowLabelsMode = 2;
            
            // Label style: 0=LabelOnly, 1=Label+Distance, 2=Label+Coords
            public int LabelStyleMode = 1;


            // Label font size slider (0..100). 100 = WhiteMediumText size, scaled down for smaller values.
            public int LabelFontSize = 80;

            // Render distance slider limits (blocks XZ)
            public int MinRenderDistance = 250;
            public int MaxRenderDistance = 1000;

            // Near-beacon fade tuning (blocks)
            public double NearFadeStartBlocks = 25.0;
            public double NearFadeEndBlocks = 10.0;


            // Default state for the "Beacon" switch when adding a new waypoint
            public bool DefaultNewWaypointBeaconOn = true;
            // Last user choice in the Add Waypoint dialog (null => use DefaultNewWaypointBeaconOn)
            public bool? LastAddBeaconChoice = null;

            // Beacon max render distance in blocks (XZ only)
            public int MaxRenderDistanceXZ = 1000;
        }

        private WaypointBeaconClientConfig clientConfig = new WaypointBeaconClientConfig();

        public bool GlobalBeaconsEnabled => clientConfig?.GlobalBeaconsEnabled ?? true;


        public bool BeamsEnabled => clientConfig?.BeamsEnabled ?? true;

        public bool ShowIconsInLabels => true;

        // 0=Always, 1=Never, 2=AutoHide
        public int ShowLabelsMode => clientConfig?.ShowLabelsMode ?? 0;


        public int LabelFontSizeSlider => clientConfig?.LabelFontSize ?? 100;

/// <summary>
/// Effective label font pixel size derived from the Label Font Size slider.
/// Slider right edge (100) matches CairoFont.WhiteMediumText() size when available.
/// </summary>
internal float GetEffectiveLabelFontPx()
{
    int slider = LabelFontSizeSlider;
    float t = GameMath.Clamp(slider, 0, 100) / 100f;

    const float minScale = 0.35f; // very small on the left
    float scale = minScale + (1f - minScale) * t;

    CairoFont baseFont = GetBaseMediumFont();
    float basePx = TryGetCairoFontPx(baseFont);
    if (basePx <= 0) basePx = 24f; // safe fallback

    return basePx * scale;
}

public void SetLabelFontSizeSlider(int slider)
{
    if (clientConfig == null) clientConfig = new WaypointBeaconClientConfig();
    int clamped = GameMath.Clamp(slider, 0, 100);
    if (clientConfig.LabelFontSize == clamped) return;

    clientConfig.LabelFontSize = clamped;
    try { capi?.StoreModConfig(clientConfig, ClientConfigFileName); } catch { }

    // Rebuild label textures so the new font size + icon size apply immediately
    labelRenderer?.DisposeAllTextures();
}

private CairoFont GetBaseMediumFont()
{
    try
    {
        var mi = typeof(CairoFont).GetMethod("WhiteMediumText", BindingFlags.Public | BindingFlags.Static);
        if (mi != null)
        {
            var f = mi.Invoke(null, null) as CairoFont;
            if (f != null) return f;
        }
    }
    catch { }

    // Fallback for older API versions
    try { return CairoFont.WhiteSmallText(); } catch { }
    return new CairoFont(24f, "Sans", new double[] { 1, 1, 1, 1 });
}

private float TryGetCairoFontPx(CairoFont font)
{
    if (font == null) return -1;

    try
    {
        var t = font.GetType();

        var prop = t.GetProperty("UnscaledFontsize") ?? t.GetProperty("FontSize") ?? t.GetProperty("Size");
        if (prop != null)
        {
            object val = prop.GetValue(font, null);
            if (val is float f) return f;
            if (val is double d) return (float)d;
            if (val is int i) return i;
        }

        var field = t.GetField("fontSize", BindingFlags.NonPublic | BindingFlags.Instance)
                 ?? t.GetField("unscaledFontsize", BindingFlags.NonPublic | BindingFlags.Instance)
                 ?? t.GetField("UnscaledFontsize", BindingFlags.NonPublic | BindingFlags.Instance);

        if (field != null)
        {
            object val = field.GetValue(font);
            if (val is float f) return f;
            if (val is double d) return (float)d;
            if (val is int i) return i;
        }
    }
    catch { }

    return -1;
}

        public int LabelStyleMode => clientConfig?.LabelStyleMode ?? 0;

        public void SetLabelStyleMode(int mode)
        {
            if (clientConfig == null) return;
            int clamped = GameMath.Clamp(mode, 0, 2);
            if (clientConfig.LabelStyleMode == clamped) return;
            clientConfig.LabelStyleMode = clamped;
            try { capi?.StoreModConfig(clientConfig, ClientConfigFileName); } catch { }

            // Distance labels change as you move; safest to rebuild textures
            labelRenderer?.DisposeAllTextures();
        }

        public void SetShowLabelsMode(int mode)
        {
            if (clientConfig == null) return;
            int clamped = GameMath.Clamp(mode, 0, 2);
            if (clientConfig.ShowLabelsMode == clamped) return;
            clientConfig.ShowLabelsMode = clamped;
            try { capi?.StoreModConfig(clientConfig, ClientConfigFileName); } catch { }
        }


        public void SetShowIconsInLabels(bool show)
        {
            if (clientConfig == null) clientConfig = new WaypointBeaconClientConfig();
            clientConfig.ShowIconsInLabels = true;

            try
            {
                capi?.StoreModConfig(clientConfig, ClientConfigFileName);
            }
            catch { }

            // Force a label refresh so any icon layout changes apply immediately
            RefreshBeaconsNow();
        }

        public void SetGlobalBeaconsEnabled(bool enabled)
        {
            if (clientConfig == null) clientConfig = new WaypointBeaconClientConfig();
            clientConfig.GlobalBeaconsEnabled = enabled;

            try
            {
                capi?.StoreModConfig(clientConfig, ClientConfigFileName);
            }
            catch { }

            RefreshBeaconsNow();
        }

        public bool NearBeaconFadeOutEnabled => clientConfig?.NearBeaconFadeOut ?? false;

        public void SetNearBeaconFadeOutEnabled(bool enabled)
        {
            if (clientConfig == null) clientConfig = new WaypointBeaconClientConfig();
            clientConfig.NearBeaconFadeOut = enabled;

            try { capi?.StoreModConfig(clientConfig, ClientConfigFileName); } catch { }

            // Renderers read this flag every frame; no heavy refresh needed.
        }


        public void SetBeamsEnabled(bool enabled)
        {
            if (clientConfig == null) clientConfig = new WaypointBeaconClientConfig();
            clientConfig.BeamsEnabled = enabled;

            try { capi?.StoreModConfig(clientConfig, ClientConfigFileName); } catch { }

            // No need to rebuild visible beacons; beam renderer reads this flag each frame.
        }



        public void ToggleGlobalBeaconsEnabled()
        {
            SetGlobalBeaconsEnabled(!GlobalBeaconsEnabled);
        }

        // ---- Configurable max render distance ----
        
        /// <summary>Gets the configurable upper limit for beacon render distance (blocks XZ).</summary>
        /// <summary>Minimum allowed value for the beacon render distance slider (blocks XZ).</summary>
public int MinRenderDistance
{
    get
    {
        int min = clientConfig?.MinRenderDistance ?? 250;
        if (min < 1) min = 1;
        if (min > 1000) min = 1000;

        int max = clientConfig?.MaxRenderDistance ?? 1000;
        if (max < min) max = min;
        if (max > 1000) max = 1000;

        if (min > max) min = max;
        return min;
    }
}

/// <summary>Maximum allowed value for the beacon render distance slider (blocks XZ).</summary>
public int MaxRenderDistance
{
    get
    {
        int max = clientConfig?.MaxRenderDistance ?? 1000;
        if (max < 1) max = 1;
        if (max > 1000) max = 1000;

        int min = clientConfig?.MinRenderDistance ?? 250;
        if (min < 1) min = 1;
        if (min > 1000) min = 1000;

        if (max < min) max = min;
        return max;
    }
}

        /// <summary>Sets the configurable upper limit for beacon render distance.</summary>
        public void SetMaxRenderDistance(int blocks)
        {
            if (clientConfig == null) clientConfig = new WaypointBeaconClientConfig();

            if (blocks < 500) blocks = 500;
            if (blocks > 10000) blocks = 10000;

            clientConfig.MaxRenderDistance = blocks;

            try { capi?.StoreModConfig(clientConfig, ClientConfigFileName); } catch { }

            RefreshBeaconsNow();
        }

        // ---- Max render distance (XZ only) ----

        /// <summary>Beacon max render distance in blocks (XZ only). Stored client-side.</summary>
        public int MaxRenderDistanceXZ
        {
            get
            {
                int val = clientConfig?.MaxRenderDistanceXZ ?? 1000;
                if (val < MinRenderDistance) val = MinRenderDistance;
                if (val > MaxRenderDistance) val = MaxRenderDistance;
                return val;
            }
        }

        public void SetMaxRenderDistanceXZ(int blocks)
        {
            if (clientConfig == null) clientConfig = new WaypointBeaconClientConfig();

            if (blocks < MinRenderDistance) blocks = MinRenderDistance;
            if (blocks > MaxRenderDistance) blocks = MaxRenderDistance;

            clientConfig.MaxRenderDistanceXZ = blocks;

            try { capi?.StoreModConfig(clientConfig, ClientConfigFileName); } catch { }

            RefreshBeaconsNow();
        }

        // Remembered default for new waypoints (Add Waypoint dialog)
        public bool DefaultNewWaypointBeaconOn => clientConfig?.DefaultNewWaypointBeaconOn ?? false;



        /// <summary>What the Add Waypoint dialog checkbox should default to.</summary>
        public bool AddDialogBeaconChoice => (clientConfig?.LastAddBeaconChoice ?? clientConfig?.DefaultNewWaypointBeaconOn) ?? false;

        public void SetLastAddBeaconChoice(bool on)
        {
            if (clientConfig == null) clientConfig = new WaypointBeaconClientConfig();
            clientConfig.LastAddBeaconChoice = on;
            try { capi?.StoreModConfig(clientConfig, ClientConfigFileName); } catch { }
        }
        public void SetDefaultNewWaypointBeaconOn(bool on)
        {
            if (clientConfig == null) clientConfig = new WaypointBeaconClientConfig();
            clientConfig.DefaultNewWaypointBeaconOn = on;

            try
            {
                capi?.StoreModConfig(clientConfig, ClientConfigFileName);
            }
            catch { }
        }

        // --------------------------------------------------------------------
        // Beacon state helpers for a live waypoint object (used by UI patch)
        // --------------------------------------------------------------------

        public bool GetBeaconOnForWaypointObject(object wpObj)
        {
            if (wpObj == null) return false;
            if (!TryGetWaypointPos(wpObj, out double x, out double y, out double z)) return false;

            string name = TryGetString(wpObj, "Title", "title", "Name", "name", "Text", "text") ?? "";
            string key = MakePinKey(x, y, z, name);

            return beaconOverrides.TryGetValue(key, out bool on) && on;
        }

        public void SetBeaconOnForWaypointObject(object wpObj, bool on)
        {
            if (wpObj == null) return;

            if (!TryGetWaypointPos(wpObj, out double x, out double y, out double z)) return;

            string name = TryGetString(wpObj, "Title", "title", "Name", "name", "Text", "text") ?? "";
            int id = GetStableWaypointId(wpObj, x, y, z, name);
            string key = MakePinKey(x, y, z, name);

            beaconOverrides[key] = on;

            // Persist + sync
            if (clientChannel?.Connected == true)
            {
                clientChannel.SendPacket(new WbSetPinnedPacket
                {
                    WaypointId = id,
                    Pinned = on,
                    X = x,
                    Y = y,
                    Z = z,
                    Title = name
                });
            }

            RefreshBeacons();
        }

        /// <summary>
        /// Marks a waypoint as 'seen' for the purposes of applying the default beacon setting
        /// only to waypoints created by other mods (i.e. without the Add dialog).
        /// </summary>
        internal void MarkWaypointSeen(object wpObj)
        {
            try
            {
                if (wpObj == null) return;
                if (!TryGetWaypointPos(wpObj, out double x, out double y, out double z)) return;
                string name = TryGetString(wpObj, "Title", "title", "Name", "name", "Text", "text") ?? "";
                string key = MakePinKey(x, y, z, name);
                if (!string.IsNullOrEmpty(key)) seenWaypointKeys.Add(key);
            }
            catch { }
        }

        internal void MarkWaypointSeenKey(string key)
        {
            if (!string.IsNullOrEmpty(key)) seenWaypointKeys.Add(key);
        }


        internal static void TrySetButtonText(GuiElementTextButton btn, string text)
        {
            if (btn == null) return;

            try
            {
                var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                var p = btn.GetType().GetProperty("Text", flags);
                if (p != null && p.CanWrite)
                {
                    p.SetValue(btn, text);
                    return;
                }

                var f = btn.GetType().GetField("Text", flags) ?? btn.GetType().GetField("text", flags);
                if (f != null) f.SetValue(btn, text);
            }
            catch
            {
                // ignore
            }
        }

        public override void StartClientSide(ICoreClientAPI api)
        {
            capi = api;


            try
            {
                clientConfig = capi.LoadModConfig<WaypointBeaconClientConfig>(ClientConfigFileName) ?? new WaypointBeaconClientConfig();
            }
            catch
            {
                clientConfig = new WaypointBeaconClientConfig();
            }
            clientChannel = capi.Network
                .RegisterChannel("waypointbeacon")
                .RegisterMessageType<WbSetPinnedPacket>()
                .RegisterMessageType<WbRequestPinsPacket>()
                .RegisterMessageType<WbPinsSyncPacket>()
                .RegisterMessageType<WbCreateWaypointPacket>();

            clientChannel.SetMessageHandler<WbPinsSyncPacket>(OnPinsSyncPacket);

            // Request saved pin overrides once the channel is connected
            capi.Event.RegisterGameTickListener(_ =>
            {
                if (requestedPins) return;
                if (clientChannel?.Connected != true) return;

                requestedPins = true;
                clientChannel.SendPacket(new WbRequestPinsPacket());
                capi.Logger.Notification("[WaypointBeacon] Requested saved pin overrides from server");
            }, 500);
            capi.Input.RegisterHotKey("waypointbeacon-togglebeacons", "Beacon Manager", GlKeys.K, HotkeyType.GUIOrOtherControls);
            capi.Input.SetHotKeyHandler("waypointbeacon-togglebeacons", OnToggleBeacons);

            capi.Input.RegisterHotKey("addbeaconwaypoint", "Add Beacon at Pointed Block", GlKeys.B, HotkeyType.GUIOrOtherControls);
            capi.Input.SetHotKeyHandler("addbeaconwaypoint", OnAddBeaconWaypointHotkey);
            capi.Logger.Warning("[WaypointBeacon] Registered B hotkey: addbeaconwaypoint");

            // Patch vanilla waypoint dialog to show a Beacon toggle companion dialog (1.21.6)
            WaypointDialogBeaconPatch.TryPatch(capi, this);

            textUtil = new TextTextureUtil(capi);
            textBg = MakeInvisibleTextBg();   // NOT null

            beamRenderer = new BeaconBeamRenderer(capi, this);
            capi.Event.RegisterRenderer(beamRenderer, ChooseBeamStage(), "waypointbeacon-beams");

            labelRenderer = new BeaconLabelRenderer(capi, this);
            capi.Event.RegisterRenderer(labelRenderer, EnumRenderStage.Ortho, "waypointbeacon-labels");

            tickListenerId = capi.Event.RegisterGameTickListener(_ => RefreshBeacons(), 250);

            capi.ShowChatMessage("[WaypointBeacon] Loaded (CGJ sentinel, matched OL/FG labels)");
        }

        public override void StartServerSide(ICoreServerAPI api)
        {
            base.StartServerSide(api);

            serverChannel = api.Network
                .RegisterChannel("waypointbeacon")
                .RegisterMessageType<WbSetPinnedPacket>()
                .RegisterMessageType<WbRequestPinsPacket>()
                .RegisterMessageType<WbPinsSyncPacket>()
                .RegisterMessageType<WbCreateWaypointPacket>();

            serverChannel.SetMessageHandler<WbSetPinnedPacket>((player, pkt) =>
            {
                HandleSetPinnedPacket(api, player, pkt);
            });

            serverChannel.SetMessageHandler<WbRequestPinsPacket>((player, pkt) =>
            {
                SendPinsToPlayer(api, player);
            });

            serverChannel.SetMessageHandler<WbCreateWaypointPacket>((player, pkt) =>
            {
                HandleCreateWaypointPacket(api, player, pkt);
            });

            // Push saved pins to players when they join (small delay so channel is ready)
            api.Event.PlayerJoin += (IServerPlayer p) =>
            {
                api.Event.RegisterCallback(_ => SendPinsToPlayer(api, p), 50);
            };
            api.Logger.Notification("[WaypointBeacon] ServerSide loaded, channel registered");

        }



        private bool OnAddBeaconWaypointHotkey(KeyCombination comb)
        {
            try
            {
                long now = DateTime.UtcNow.Ticks;
                if (now - lastAddWaypointHotkeyTicks < TimeSpan.FromMilliseconds(250).Ticks)
                {
                    return true;
                }
                lastAddWaypointHotkeyTicks = now;

                capi?.Logger?.Warning("[WaypointBeacon] B hotkey handler triggered");

                BlockPos rawTarget = capi.World.Player.CurrentBlockSelection?.Position
                    ?? capi.World.Player.Entity.Pos.AsBlockPos;

                BlockPos target = NormalizeTargetBlockPos(rawTarget);
                if (rawTarget != null && (rawTarget.X != target.X || rawTarget.Z != target.Z))
                {
                    capi?.Logger?.Notification("[WaypointBeacon] Normalized target coords raw={0},{1},{2} -> norm={3},{4},{5}", rawTarget.X, rawTarget.Y, rawTarget.Z, target.X, target.Y, target.Z);
                }

                bool opened = TryRequestServerWaypointAndOpenEdit(target);
                if (!opened)
                {
                    // Last resort legacy path
                    opened = TryCreateInstantWaypointAndOpenEdit(target);
                }
                if (!opened)
                {
                    capi?.Logger?.Warning("[WaypointBeacon] Add Beacon hotkey failed to create/edit waypoint at {0},{1},{2}", target.X, target.Y, target.Z);
                }

                return opened;
            }
            catch (Exception e)
            {
                capi?.Logger?.Error("[WaypointBeacon] Failed to open Add Beacon dialog: {0}", e);
                return false;
            }
        }

        private bool TryRequestServerWaypointAndOpenEdit(BlockPos target)
        {
            try
            {
                if (clientChannel?.Connected != true) return false;

                var beforeKeys = SnapshotWaypointKeys();
                var pkt = new WbCreateWaypointPacket
                {
                    X = target.X,
                    Y = target.Y,
                    Z = target.Z,
                    Title = "New Beacon",
                    Icon = "circle",
                    Color = "#ffd700"
                };

                clientChannel.SendPacket(pkt);
                capi?.Logger?.Notification("[WaypointBeacon] Requested server waypoint create at {0},{1},{2}", target.X, target.Y, target.Z);

                capi?.Event?.RegisterCallback(_ => RetryOpenServerCreatedWaypoint(beforeKeys, target, 0, true), 50);
                return true;
            }
            catch (Exception e)
            {
                capi?.Logger?.Warning("[WaypointBeacon] Failed requesting server waypoint create: {0}", e.Message);
                return false;
            }
        }

        private void RetryOpenServerCreatedWaypoint(HashSet<string> beforeKeys, BlockPos target, int attempt, bool allowFallback)
        {
            try
            {
                if (attempt > 40)
                {
                    capi?.Logger?.Warning("[WaypointBeacon] Timed out waiting for server-created waypoint sync at {0},{1},{2}", target.X, target.Y, target.Z);
                    if (allowFallback)
                    {
                        bool opened = TryCreateInstantWaypointAndOpenEdit(target);
                        capi?.Logger?.Warning("[WaypointBeacon] Server create timed out, legacy fallback open result={0}", opened);
                    }
                    return;
                }

                object createdWaypoint = FindNewWaypointBySnapshot(beforeKeys, target.X + 0.5, target.Y + 0.5, target.Z + 0.5);
                if (createdWaypoint != null)
                {
                    var mapManager = capi?.ModLoader?.GetModSystem<WorldMapManager>();
                    object waypointLayer = GetWaypointMapLayerObject(mapManager);
                    if (waypointLayer != null && TryOpenEditDialogForWaypoint(waypointLayer, createdWaypoint))
                    {
                        capi?.Logger?.Notification("[WaypointBeacon] Opened Edit for server-created waypoint at {0},{1},{2}", target.X, target.Y, target.Z);
                        return;
                    }
                }

                capi?.Event?.RegisterCallback(_ => RetryOpenServerCreatedWaypoint(beforeKeys, target, attempt + 1, allowFallback), 50);
            }
            catch { }
        }

        private BlockPos NormalizeTargetBlockPos(BlockPos target)
        {
            if (target == null) return new BlockPos(0, 0, 0);

            int mapSizeX = capi?.World?.BlockAccessor?.MapSizeX ?? 0;
            int mapSizeZ = capi?.World?.BlockAccessor?.MapSizeZ ?? 0;

            int x = NormalizeWrappedCoord(target.X, mapSizeX);
            int z = NormalizeWrappedCoord(target.Z, mapSizeZ);

            // Prefer the wrapped coordinate nearest to the player's actual position.
            double? playerX = capi?.World?.Player?.Entity?.Pos?.X;
            double? playerZ = capi?.World?.Player?.Entity?.Pos?.Z;
            x = ResolveClosestWrappedCoord(x, playerX, mapSizeX);
            z = ResolveClosestWrappedCoord(z, playerZ, mapSizeZ);

            // Some environments report 0/invalid map size here; apply fallback
            // for wrapped-space values like 2558xx that should be negative coords.
            if (mapSizeX <= 0) x = NormalizeLikelyWrappedCoord(x);
            if (mapSizeZ <= 0) z = NormalizeLikelyWrappedCoord(z);

            return new BlockPos(x, target.Y, z);
        }

        private int ResolveClosestWrappedCoord(int value, double? reference, int mapSize)
        {
            if (!reference.HasValue || mapSize <= 0) return value;

            int best = value;
            double bestDist = Math.Abs(value - reference.Value);

            // Check nearby wrapped variants and pick the nearest to player position.
            for (int k = -3; k <= 3; k++)
            {
                int candidate = value + (k * mapSize);
                double dist = Math.Abs(candidate - reference.Value);
                if (dist < bestDist)
                {
                    bestDist = dist;
                    best = candidate;
                }
            }

            return best;
        }

        private int NormalizeLikelyWrappedCoord(int value)
        {
            if (value > 200000) return value - 256000;
            if (value < -200000) return value + 256000;
            return value;
        }

        private bool TryCreateInstantWaypointAndOpenEdit(BlockPos target)
        {
            var mapManager = capi?.ModLoader?.GetModSystem<WorldMapManager>();
            object waypointLayer = GetWaypointMapLayerObject(mapManager);
            if (waypointLayer == null) return false;

            HashSet<string> beforeKeys = SnapshotWaypointKeys();

            object waypoint = CreateGenericWaypoint(target);
            if (waypoint == null) return false;

            bool added = TryAddWaypointToLayer(waypointLayer, waypoint, out string addMethodName);
            if (!added)
            {
                added = TryCreateWaypointViaChatCommand(target, "New Beacon");
                if (added)
                {
                    addMethodName = "chat-command";
                }
            }
            if (!added) return false;

            object createdWaypoint = FindNewWaypointBySnapshot(beforeKeys, target.X + 0.5, target.Y + 0.5, target.Z + 0.5);
            if (createdWaypoint == null)
            {
                if (string.Equals(addMethodName, "chat-command", StringComparison.OrdinalIgnoreCase))
                {
                    capi?.Logger?.Notification("[WaypointBeacon] Waiting for waypoint list update after /waypoint command...");
                    capi?.Event?.RegisterCallback(_ => RetryOpenCreatedWaypointFromSnapshot(beforeKeys, target, 0), 50);
                    return true;
                }

                capi?.Logger?.Warning("[WaypointBeacon] Added waypoint via {0}, but could not identify a newly created persistent waypoint.", addMethodName ?? "unknown method");
                return false;
            }

            if (!TryOpenEditDialogForWaypoint(waypointLayer, createdWaypoint)) return false;

            capi?.Logger?.Notification("[WaypointBeacon] Created waypoint and opened Edit dialog at {0},{1},{2}", target.X, target.Y, target.Z);
            return true;
        }



        private void RetryOpenCreatedWaypointFromSnapshot(HashSet<string> beforeKeys, BlockPos target, int attempt)
        {
            try
            {
                if (attempt > 40)
                {
                    capi?.Logger?.Warning("[WaypointBeacon] Timed out waiting for /waypoint-created marker sync at {0},{1},{2}", target.X, target.Y, target.Z);
                    return;
                }

                object createdWaypoint = FindNewWaypointBySnapshot(beforeKeys, target.X + 0.5, target.Y + 0.5, target.Z + 0.5);
                if (createdWaypoint != null)
                {
                    var mapManager = capi?.ModLoader?.GetModSystem<WorldMapManager>();
                    object waypointLayer = GetWaypointMapLayerObject(mapManager);
                    if (waypointLayer != null && TryOpenEditDialogForWaypoint(waypointLayer, createdWaypoint))
                    {
                        capi?.Logger?.Notification("[WaypointBeacon] Opened Edit for /waypoint-created marker at {0},{1},{2}", target.X, target.Y, target.Z);
                        return;
                    }
                }

                capi?.Event?.RegisterCallback(_ => RetryOpenCreatedWaypointFromSnapshot(beforeKeys, target, attempt + 1), 50);
            }
            catch { }
        }

        private bool TryCreateWaypointViaChatCommand(BlockPos target, string title)
        {
            try
            {
                BlockPos norm = NormalizeTargetBlockPos(target);
                string safeTitle = string.IsNullOrWhiteSpace(title) ? "New WPB" : title.Replace("\"", "");

                string cmd = $"/waypoint addat {norm.X} {norm.Y} {norm.Z} false #FFFFFF \"{safeTitle}\"";
                capi?.Logger?.Notification("[WaypointBeacon] Attempting waypoint chat command: {0}", cmd);
                capi?.SendChatMessage(cmd);
                return true;
            }
            catch (Exception e)
            {
                capi?.Logger?.Debug("[WaypointBeacon] Chat-command waypoint add failed: {0}", e);
            }

            return false;
        }


        private object CreateGenericWaypoint(BlockPos target)
        {
            try
            {
                var wp = new Waypoint();
                double px = target.X + 0.5;
                double py = target.Y + 0.5;
                double pz = target.Z + 0.5;
                int waypointId = GetNextWaypointId();

                TrySetMemberValue(wp, "Position", new Vec3d(px, py, pz));
                TrySetMemberValue(wp, "Pos", new Vec3d(px, py, pz));
                TrySetMemberValue(wp, "X", px);
                TrySetMemberValue(wp, "Y", py);
                TrySetMemberValue(wp, "Z", pz);

                TrySetMemberValue(wp, "Title", "New Beacon");
                TrySetMemberValue(wp, "Text", "New Beacon");
                TrySetMemberValue(wp, "Name", "New Beacon");

                TrySetMemberValue(wp, "Color", "#ffd700");
                TrySetMemberValue(wp, "color", "#ffd700");
                TrySetMemberValue(wp, "Icon", "circle");
                TrySetMemberValue(wp, "Symbol", "circle");

                // Critical: if ID stays 0, save can overwrite Waypoint 0 instead of this new entry.
                TrySetMemberValue(wp, "WaypointID", waypointId);
                TrySetMemberValue(wp, "WaypointId", waypointId);
                TrySetMemberValue(wp, "Id", waypointId);

                string playerUid = capi?.World?.Player?.PlayerUID;
                if (!string.IsNullOrEmpty(playerUid))
                {
                    TrySetMemberValue(wp, "OwningPlayerUid", playerUid);
                    TrySetMemberValue(wp, "OwnerUid", playerUid);
                    TrySetMemberValue(wp, "PlayerUid", playerUid);
                }

                capi?.Logger?.Notification("[WaypointBeacon] Seeded new waypoint with id={0} at {1},{2},{3}", waypointId, target.X, target.Y, target.Z);
                return wp;
            }
            catch (Exception e)
            {
                capi?.Logger?.Warning("[WaypointBeacon] Failed to create generic waypoint: {0}", e);
                return null;
            }
        }


        private int GetNextWaypointId()
        {
            int maxId = 0;

            foreach (var wp in EnumerateWaypoints())
            {
                int id = TryGetInt(wp, "WaypointID", "WaypointId", "WaypointIDInt", "Id", "ID", "id") ?? 0;
                if (id > maxId) maxId = id;
            }

            // Keep away from 0, which is often reused by old/default waypoints in client state.
            return maxId + 1;
        }

        private bool TryAddWaypointToLayer(object waypointLayer, object waypoint, out string methodUsed)
        {
            methodUsed = null;

            try
            {
                int candidateCount = 0;
                var preferredNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "AddWaypoint", "AddWayPoint", "AddOwnWaypoint", "CreateWaypoint", "CreateWayPoint"
                };

                var methods = waypointLayer.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    .Where(m =>
                    {
                        string n = (m.Name ?? string.Empty).ToLowerInvariant();
                        if (!n.Contains("waypoint")) return false;
                        if (!n.Contains("add") && !n.Contains("create")) return false;
                        if (n.StartsWith("oncmd")) return false;
                        if (n.Contains("temporary") || n.Contains("temp")) return false;
                        return true;
                    })
                    .OrderByDescending(m => preferredNames.Contains(m.Name ?? string.Empty))
                    .ThenBy(m => m.GetParameters().Length);

                foreach (var method in methods)
                {
                    candidateCount++;
                    var ps = method.GetParameters();
                    if (!ps.Any(p => p.ParameterType.IsInstanceOfType(waypoint))) continue;
                    if (ps.Any(p => typeof(IServerPlayer).IsAssignableFrom(p.ParameterType))) continue;

                    object[] args = new object[ps.Length];
                    bool badArgs = false;

                    for (int i = 0; i < ps.Length; i++)
                    {
                        Type pt = ps[i].ParameterType;

                        if (pt.IsInstanceOfType(waypoint)) args[i] = waypoint;
                        else if (typeof(ICoreClientAPI).IsAssignableFrom(pt)) args[i] = capi;
                        else if (typeof(ICoreAPI).IsAssignableFrom(pt)) args[i] = capi;
                        else if (typeof(IPlayer).IsAssignableFrom(pt)) args[i] = capi?.World?.Player;
                        else if (typeof(WorldMapManager).IsAssignableFrom(pt)) args[i] = capi?.ModLoader?.GetModSystem<WorldMapManager>();
                        else if (typeof(IWorldAccessor).IsAssignableFrom(pt)) args[i] = capi?.World;
                        else if (pt.IsValueType) args[i] = Activator.CreateInstance(pt);
                        else if (!pt.IsClass) { badArgs = true; break; }
                        else args[i] = null;
                    }

                    if (badArgs) continue;

                    try
                    {
                        object ret = method.Invoke(waypointLayer, args);
                        if (ret is bool b && !b) continue;

                        methodUsed = method.Name;
                        capi?.Logger?.Notification("[WaypointBeacon] Added waypoint using {0}.{1}", waypointLayer.GetType().Name, method.Name);
                        return true;
                    }
                    catch (Exception invokeEx)
                    {
                        capi?.Logger?.Debug("[WaypointBeacon] Add waypoint invoke failed on {0}.{1}: {2}", waypointLayer.GetType().Name, method.Name, invokeEx.InnerException ?? invokeEx);
                    }
                }
            }
            catch (Exception e)
            {
                capi?.Logger?.Debug("[WaypointBeacon] Failed scanning layer add-waypoint methods: {0}", e);
            }

            capi?.Logger?.Warning("[WaypointBeacon] No compatible persistent add-waypoint method succeeded on {0}", waypointLayer?.GetType()?.Name ?? "unknown-layer");
            return false;
        }

        private HashSet<string> SnapshotWaypointKeys()
        {
            var keys = new HashSet<string>();

            foreach (var wp in EnumerateWaypoints())
            {
                if (!TryGetWaypointPos(wp, out double x, out double y, out double z)) continue;
                string title = TryGetString(wp, "Title", "title", "Name", "name", "Text", "text") ?? string.Empty;
                keys.Add(MakePinKey(x, y, z, title));
            }

            return keys;
        }

        private object FindNewWaypointBySnapshot(HashSet<string> beforeKeys, double x, double y, double z)
        {
            object nearest = null;
            double bestDistSq = double.MaxValue;

            foreach (var wp in EnumerateWaypoints())
            {
                if (!TryGetWaypointPos(wp, out double wx, out double wy, out double wz)) continue;
                string title = TryGetString(wp, "Title", "title", "Name", "name", "Text", "text") ?? string.Empty;
                string key = MakePinKey(wx, wy, wz, title);

                if (beforeKeys != null && beforeKeys.Contains(key)) continue;

                double dx = wx - x;
                double dy = wy - y;
                double dz = wz - z;
                double distSq = dx * dx + dy * dy + dz * dz;

                if (distSq < bestDistSq)
                {
                    bestDistSq = distSq;
                    nearest = wp;
                }
            }

            return nearest;
        }

        private bool TryOpenEditDialogForWaypoint(object waypointLayer, object waypoint)
        {
            if (waypointLayer == null || waypoint == null) return false;

            int waypointId = TryGetInt(waypoint, "WaypointID", "WaypointId", "WaypointIDInt", "Id", "ID", "id") ?? -1;
            int waypointIndex = TryGetWaypointIndexInLayer(waypointLayer, waypoint);

            try
            {
                var editMethods = waypointLayer.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    .Where(m =>
                    {
                        string n = (m.Name ?? string.Empty).ToLowerInvariant();
                        return n.Contains("edit") && n.Contains("waypoint");
                    })
                    .OrderBy(m => m.GetParameters().Length)
                    .ToArray();

                foreach (var method in editMethods)
                {
                    if (!TryBuildEditInvocationArgs(method.GetParameters(), waypoint, waypointId, waypointIndex, out object[] args))
                    {
                        continue;
                    }

                    try
                    {
                        object ret = method.Invoke(waypointLayer, args);
                        if (ret is bool b && !b) continue;
                        capi?.Logger?.Notification("[WaypointBeacon] Opened edit dialog using {0}.{1} (id={2}, index={3})", waypointLayer.GetType().Name, method.Name, waypointId, waypointIndex);
                        return true;
                    }
                    catch (Exception invokeEx)
                    {
                        capi?.Logger?.Debug("[WaypointBeacon] Edit waypoint invoke failed on {0}.{1}: {2}", waypointLayer.GetType().Name, method.Name, invokeEx.InnerException ?? invokeEx);
                    }
                }
            }
            catch (Exception e)
            {
                capi?.Logger?.Debug("[WaypointBeacon] Failed to locate edit-waypoint method: {0}", e);
            }

            try
            {
                var ctors = typeof(GuiDialogEditWayPoint).GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    .OrderBy(c => c.GetParameters().Length)
                    .ToArray();

                foreach (var ctor in ctors)
                {
                    if (!TryBuildEditInvocationArgs(ctor.GetParameters(), waypoint, waypointId, waypointIndex, out object[] args, waypointLayer))
                    {
                        continue;
                    }

                    var dlg = ctor.Invoke(args) as GuiDialogEditWayPoint;
                    if (dlg?.TryOpen() == true)
                    {
                        capi?.Logger?.Notification("[WaypointBeacon] Opened edit dialog via ctor {0} (id={1}, index={2})", ctor, waypointId, waypointIndex);
                        return true;
                    }
                }
            }
            catch (Exception e)
            {
                capi?.Logger?.Debug("[WaypointBeacon] Failed ctor fallback for edit dialog: {0}", e);
            }

            return false;
        }

        private int TryGetWaypointIndexInLayer(object waypointLayer, object waypoint)
        {
            try
            {
                object listObj =
                    TryGetMember(waypointLayer, "ownWaypoints") ??
                    TryGetMember(waypointLayer, "OwnWaypoints") ??
                    TryGetMember(waypointLayer, "waypoints") ??
                    TryGetMember(waypointLayer, "Waypoints");

                if (!(listObj is IList list)) return -1;
                for (int i = 0; i < list.Count; i++)
                {
                    if (ReferenceEquals(list[i], waypoint)) return i;
                }
            }
            catch { }

            return -1;
        }

        private bool TryBuildEditInvocationArgs(ParameterInfo[] ps, object waypoint, int waypointId, int waypointIndex, out object[] args, object waypointLayer = null)
        {
            args = new object[ps.Length];

            for (int i = 0; i < ps.Length; i++)
            {
                var pt = ps[i].ParameterType;
                string pn = (ps[i].Name ?? string.Empty).ToLowerInvariant();

                if (pt.IsInstanceOfType(waypoint)) { args[i] = waypoint; continue; }
                if (waypointLayer != null && pt.IsInstanceOfType(waypointLayer)) { args[i] = waypointLayer; continue; }
                if (typeof(ICoreClientAPI).IsAssignableFrom(pt)) { args[i] = capi; continue; }
                if (typeof(ICoreAPI).IsAssignableFrom(pt)) { args[i] = capi; continue; }
                if (typeof(IPlayer).IsAssignableFrom(pt)) { args[i] = capi?.World?.Player; continue; }
                if (typeof(WorldMapManager).IsAssignableFrom(pt)) { args[i] = capi?.ModLoader?.GetModSystem<WorldMapManager>(); continue; }
                if (typeof(IWorldAccessor).IsAssignableFrom(pt)) { args[i] = capi?.World; continue; }

                if (pt == typeof(int) || pt == typeof(long))
                {
                    bool wantsId = pn.Contains("id");
                    bool wantsIndex = pn.Contains("index") || pn.Contains("idx");

                    if (wantsId)
                    {
                        if (waypointId < 0) return false;
                        args[i] = pt == typeof(long) ? (object)(long)waypointId : waypointId;
                        continue;
                    }

                    if (wantsIndex)
                    {
                        if (waypointIndex < 0) return false;
                        args[i] = pt == typeof(long) ? (object)(long)waypointIndex : waypointIndex;
                        continue;
                    }

                    // ambiguous numeric waypoint selector: prefer index, then id
                    if (waypointIndex >= 0)
                    {
                        args[i] = pt == typeof(long) ? (object)(long)waypointIndex : waypointIndex;
                        continue;
                    }

                    if (waypointId >= 0)
                    {
                        args[i] = pt == typeof(long) ? (object)(long)waypointId : waypointId;
                        continue;
                    }

                    return false;
                }

                if (pt.IsValueType)
                {
                    args[i] = Activator.CreateInstance(pt);
                    continue;
                }

                args[i] = null;
            }

            return true;
        }

        private bool TrySetMemberValue(object obj, string memberName, object value)
        {
            if (obj == null || string.IsNullOrEmpty(memberName)) return false;

            var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            Type t = obj.GetType();

            try
            {
                var prop = t.GetProperty(memberName, flags);
                if (prop != null && prop.CanWrite)
                {
                    object converted = ConvertValueForType(value, prop.PropertyType);
                    if (converted != null || !prop.PropertyType.IsValueType)
                    {
                        prop.SetValue(obj, converted);
                        return true;
                    }
                }

                var field = t.GetField(memberName, flags);
                if (field != null)
                {
                    object converted = ConvertValueForType(value, field.FieldType);
                    if (converted != null || !field.FieldType.IsValueType)
                    {
                        field.SetValue(obj, converted);
                        return true;
                    }
                }
            }
            catch { }

            return false;
        }

        private object ConvertValueForType(object value, Type targetType)
        {
            if (targetType == null) return value;
            if (value == null) return targetType.IsValueType ? Activator.CreateInstance(targetType) : null;

            if (targetType.IsInstanceOfType(value)) return value;

            try
            {
                if (targetType.IsEnum)
                {
                    if (value is string s) return Enum.Parse(targetType, s, true);
                    return Enum.ToObject(targetType, value);
                }

                if (targetType == typeof(string)) return value.ToString();
                if (targetType == typeof(Vec3d) && value is BlockPos bp) return new Vec3d(bp.X + 0.5, bp.Y + 0.5, bp.Z + 0.5);

                return Convert.ChangeType(value, targetType);
            }
            catch
            {
                return null;
            }
        }

        private void TrySeedCreatingWaypointPosition(GuiDialogAddWayPoint dlg, BlockPos target)
        {
            try
            {
                var flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
                var dt = dlg.GetType();

                var wpField = dt.GetField("creatingWaypoint", flags) ?? dt.GetField("CreatingWaypoint", flags);
                object wpObj = wpField?.GetValue(dlg);

                Vec3d pos = new Vec3d(target.X + 0.5, target.Y + 0.5, target.Z + 0.5);

                if (wpObj != null)
                {
                    var wpt = wpObj.GetType();
                    var posProp = wpt.GetProperty("Position", flags);
                    if (posProp != null && posProp.CanWrite && posProp.PropertyType == typeof(Vec3d))
                    {
                        posProp.SetValue(wpObj, pos);
                        return;
                    }

                    var posField = wpt.GetField("Position", flags) ?? wpt.GetField("position", flags);
                    if (posField != null && posField.FieldType == typeof(Vec3d))
                    {
                        posField.SetValue(wpObj, pos);
                        return;
                    }
                }

                // Fallback: dialog-level position members if creatingWaypoint wasn't initialized yet.
                TrySetIntMember(dlg, dt, flags, target.X, "x", "X", "posX", "targetX", "waypointX");
                TrySetIntMember(dlg, dt, flags, target.Y, "y", "Y", "posY", "targetY", "waypointY");
                TrySetIntMember(dlg, dt, flags, target.Z, "z", "Z", "posZ", "targetZ", "waypointZ");
            }
            catch (Exception e)
            {
                capi?.Logger?.Debug("[WaypointBeacon] Failed to seed creatingWaypoint position: {0}", e);
            }
        }

        private bool TryOpenAddWaypointDialogDirect()
        {
            // Preferred for VS 1.21.6: call SystemHotkeys add-waypoint entrypoint.
            if (TryInvokeSystemHotkeysAddWaypoint())
            {
                return true;
            }

            // No ctor fallback here: ctor-opened dialogs were causing save crashes.
            return false;
        }

        private bool TryGetLookAtBlockPos(out int x, out int y, out int z)
        {
            x = y = z = 0;

            try
            {
                var sel = capi?.World?.Player?.CurrentBlockSelection;
                if (sel?.Position == null) return false;

                int rawX = sel.Position.X;
                int rawY = sel.Position.Y;
                int rawZ = sel.Position.Z;

                int mapSizeX = capi?.World?.BlockAccessor?.MapSizeX ?? 0;
                int mapSizeZ = capi?.World?.BlockAccessor?.MapSizeZ ?? 0;

                x = NormalizeWrappedCoord(rawX, mapSizeX);
                y = rawY;
                z = NormalizeWrappedCoord(rawZ, mapSizeZ);

                if (rawX != x || rawZ != z)
                {
                    capi?.Logger?.Notification("[WaypointBeacon] Add Waypoint (Direct): normalized look-pos raw={0},{1},{2} -> norm={3},{4},{5} (map={6}x{7})", rawX, rawY, rawZ, x, y, z, mapSizeX, mapSizeZ);
                }

                return true;
            }
            catch
            {
                return false;
            }
        }

        private int NormalizeWrappedCoord(int value, int mapSize)
        {
            if (mapSize <= 0) return value;

            int half = mapSize / 2;
            if (value > half) return value - mapSize;
            if (value < -half) return value + mapSize;
            return value;
        }

        private void ApplyInitialAddWaypointPosition(GuiDialogAddWayPoint dlg, int x, int y, int z)
        {
            if (dlg == null) return;

            try
            {
                var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                var dt = dlg.GetType();

                bool applied = false;

                // 1) Try direct XYZ members on dialog
                applied |= TrySetIntMember(dlg, dt, flags, x, "x", "X", "posX", "targetX", "waypointX");
                applied |= TrySetIntMember(dlg, dt, flags, y, "y", "Y", "posY", "targetY", "waypointY");
                applied |= TrySetIntMember(dlg, dt, flags, z, "z", "Z", "posZ", "targetZ", "waypointZ");

                // 2) Try nested position object members
                foreach (string posName in new[] { "position", "Position", "pos", "Pos", "targetPos", "TargetPos", "waypointPos", "WaypointPos" })
                {
                    object posObj = TryGetMember(dlg, posName);
                    if (posObj == null) continue;

                    var pt = posObj.GetType();
                    bool px = TrySetIntMember(posObj, pt, flags, x, "x", "X");
                    bool py = TrySetIntMember(posObj, pt, flags, y, "y", "Y");
                    bool pz = TrySetIntMember(posObj, pt, flags, z, "z", "Z");
                    if (px || py || pz) applied = true;
                }

                capi?.Logger?.Notification("[WaypointBeacon] Add Waypoint (Direct): look-pos {0},{1},{2} applied={3}", x, y, z, applied);
            }
            catch (Exception e)
            {
                capi?.Logger?.Debug("[WaypointBeacon] Add Waypoint (Direct): failed applying look-pos: {0}", e);
            }
        }

        private bool TrySetIntMember(object obj, Type t, BindingFlags flags, int value, params string[] names)
        {
            foreach (string name in names)
            {
                try
                {
                    var p = t.GetProperty(name, flags);
                    if (p != null && p.CanWrite)
                    {
                        if (p.PropertyType == typeof(int)) { p.SetValue(obj, value); return true; }
                        if (p.PropertyType == typeof(double)) { p.SetValue(obj, (double)value); return true; }
                        if (p.PropertyType == typeof(float)) { p.SetValue(obj, (float)value); return true; }
                    }

                    var f = t.GetField(name, flags);
                    if (f != null)
                    {
                        if (f.FieldType == typeof(int)) { f.SetValue(obj, value); return true; }
                        if (f.FieldType == typeof(double)) { f.SetValue(obj, (double)value); return true; }
                        if (f.FieldType == typeof(float)) { f.SetValue(obj, (float)value); return true; }
                    }
                }
                catch { }
            }

            return false;
        }

        private bool TryInvokeSystemHotkeysAddWaypoint()
        {
            try
            {
                var loader = capi?.ModLoader;
                if (loader == null) return false;

                // First try exact type lookup for VS 1.21.6
                object sysHotkeys = ResolveSystemHotkeysInstance(loader);
                if (sysHotkeys != null && TryInvokeKnownSystemHotkeysMethods(sysHotkeys))
                {
                    return true;
                }

                // Fallback: scan loader objects for SystemHotkeys-like systems
                foreach (var sys in EnumerateLoaderObjects(loader))
                {
                    if (sys == null) continue;
                    var t = sys.GetType();
                    string tn = t.FullName ?? t.Name ?? "";
                    if (tn.IndexOf("SystemHotkeys", StringComparison.OrdinalIgnoreCase) < 0) continue;

                    if (TryInvokeKnownSystemHotkeysMethods(sys))
                    {
                        return true;
                    }
                }
            }
            catch (Exception e)
            {
                capi?.Logger?.Debug("[WaypointBeacon] SystemHotkeys add-waypoint path failed: {0}", e);
            }

            return false;
        }

        private object ResolveSystemHotkeysInstance(object loader)
        {
            try
            {
                Type sysType = null;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    sysType = asm.GetType("Vintagestory.Client.SystemHotkeys", false, false);
                    if (sysType != null) break;
                }
                if (sysType == null) return null;

                var m = loader.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    .FirstOrDefault(mi => mi.Name == "GetModSystem"
                        && mi.GetParameters().Length == 1
                        && mi.GetParameters()[0].ParameterType == typeof(Type));
                if (m == null) return null;

                return m.Invoke(loader, new object[] { sysType });
            }
            catch
            {
                return null;
            }
        }

        private bool TryInvokeKnownSystemHotkeysMethods(object sys)
        {
            var t = sys.GetType();
            var names = new[]
            {
                "OnAddWaypoint", "OnAddWayPoint", "AddWaypoint", "AddWayPoint",
                "OnHotkeyAddWaypoint", "OnHotKeyAddWaypoint"
            };

            foreach (string n in names)
            {
                var methods = t.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    .Where(m => string.Equals(m.Name, n, StringComparison.OrdinalIgnoreCase))
                    .Where(IsSafeAddWaypointMethod)
                    .OrderBy(m => m.GetParameters().Length)
                    .ToArray();

                foreach (var m in methods)
                {
                    if (!TryInvokeAddWaypointMethod(sys, m)) continue;

                    capi?.Logger?.Notification("[WaypointBeacon] Opened Add Waypoint via fixed v1.21.6 hotkey method: {0}.{1}", t.FullName, m.Name);
                    return true;
                }
            }

            return false;
        }

        private IEnumerable<object> EnumerateLoaderObjects(object loader)
        {
            var seen = new HashSet<object>();
            foreach (string name in new[] { "systems", "Systems", "modsystems", "ModSystems", "clientSystems", "loadedSystems", "LoadedSystems" })
            {
                object val = TryGetMember(loader, name);
                foreach (var obj in FlattenAny(val))
                {
                    if (obj != null && seen.Add(obj)) yield return obj;
                }
            }

            var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var t = loader.GetType();
            foreach (var f in t.GetFields(flags))
            {
                if (!f.Name.ToLowerInvariant().Contains("system")) continue;
                object val = null;
                try { val = f.GetValue(loader); } catch { }
                foreach (var obj in FlattenAny(val))
                {
                    if (obj != null && seen.Add(obj)) yield return obj;
                }
            }
        }

        private IEnumerable<object> FlattenAny(object val)
        {
            if (val == null) yield break;
            if (val is System.Collections.IDictionary d)
            {
                foreach (System.Collections.DictionaryEntry de in d) if (de.Value != null) yield return de.Value;
                yield break;
            }
            if (val is System.Collections.IEnumerable en && !(val is string))
            {
                foreach (var item in en)
                {
                    if (item == null) continue;
                    object v = TryGetMember(item, "Value") ?? item;
                    if (v != null) yield return v;
                }
                yield break;
            }
            yield return val;
        }

        private bool TryTriggerVanillaAddWaypointByCode()
        {
            try
            {
                var input = capi?.Input;
                if (input == null) return false;

                var codes = new List<string>
                {
                    "addwaypoint", "add-waypoint", "addWaypoint", "worldmap-addwaypoint", "hotkey-addwaypoint"
                };

                // Discover extra candidate codes from registered hotkeys
                try
                {
                    object hotkeysObj =
                        TryGetMember(input, "HotKeys") ??
                        TryGetMember(input, "hotKeys") ??
                        TryGetMember(input, "Hotkeys") ??
                        TryGetMember(input, "hotkeys");

                    if (hotkeysObj is System.Collections.IDictionary dict)
                    {
                        foreach (System.Collections.DictionaryEntry de in dict)
                        {
                            if (!IsLikelyAddWaypointHotkey(de.Value)) continue;
                            string c = (TryGetMember(de.Value, "Code") ?? TryGetMember(de.Value, "code") ?? "").ToString();
                            if (!string.IsNullOrWhiteSpace(c) && !codes.Contains(c)) codes.Add(c);
                        }
                    }
                }
                catch { }

                var methods = input.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    .Where(m => m.Name.IndexOf("trigger", StringComparison.OrdinalIgnoreCase) >= 0 &&
                                m.Name.IndexOf("hotkey", StringComparison.OrdinalIgnoreCase) >= 0)
                    .ToArray();

                foreach (var code in codes)
                {
                    foreach (var m in methods)
                    {
                        if (TryInvokeTriggerMethod(input, m, code))
                        {
                            capi?.Logger?.Notification("[WaypointBeacon] Opened Add Waypoint via trigger-by-code '{0}' using {1}", code, m.Name);
                            return true;
                        }
                    }
                }
            }
            catch (Exception e)
            {
                capi?.Logger?.Debug("[WaypointBeacon] Trigger-by-code path failed: {0}", e);
            }

            return false;
        }

        private bool TryInvokeTriggerMethod(object target, MethodInfo method, string hotkeyCode)
        {
            try
            {
                var ps = method.GetParameters();
                object[] args = new object[ps.Length];

                for (int i = 0; i < ps.Length; i++)
                {
                    var pt = ps[i].ParameterType;
                    if (pt == typeof(string))
                    {
                        args[i] = hotkeyCode;
                    }
                    else if (typeof(KeyCombination).IsAssignableFrom(pt))
                    {
                        args[i] = CreateDirectAddWaypointKeyCombination();
                    }
                    else if (pt == typeof(bool))
                    {
                        // conservative defaults for optional trigger flags
                        args[i] = false;
                    }
                    else if (typeof(ICoreClientAPI).IsAssignableFrom(pt)) args[i] = capi;
                    else if (typeof(ICoreAPI).IsAssignableFrom(pt)) args[i] = capi;
                    else if (typeof(IWorldAccessor).IsAssignableFrom(pt)) args[i] = capi?.World;
                    else if (typeof(IPlayer).IsAssignableFrom(pt)) args[i] = capi?.World?.Player;
                    else if (pt.IsValueType) args[i] = Activator.CreateInstance(pt);
                    else args[i] = null;
                }

                object ret = method.Invoke(target, args);
                if (ret is bool b) return b;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private bool TryInvokeVanillaAddWaypointHotkey()
        {
            try
            {
                var input = capi?.Input;
                if (input == null) return false;

                var hk = FindVanillaAddWaypointHotkey(input);
                if (hk == null) return false;

                var comb = CreateDirectAddWaypointKeyCombination();
                if (comb == null) comb = new KeyCombination();

                // 1) Try callable methods first.
                var methods = hk.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                foreach (var m in methods)
                {
                    string n = m.Name.ToLowerInvariant();
                    if (!(n.Contains("trigger") || n.Contains("invoke") || n.Contains("handler") || n.Contains("execute"))) continue;

                    if (TryInvokeAddWaypointMethod(hk, m))
                    {
                        capi?.Logger?.Notification("[WaypointBeacon] Opened Add Waypoint via vanilla hotkey method {0}.{1}", hk.GetType().Name, m.Name);
                        return true;
                    }

                    // Direct invoke with our keycomb if it expects exactly one KeyCombination.
                    var ps = m.GetParameters();
                    if (ps.Length == 1 && typeof(KeyCombination).IsAssignableFrom(ps[0].ParameterType))
                    {
                        try
                        {
                            object ret = m.Invoke(hk, new object[] { comb });
                            if (ret is bool b && !b) continue;
                            capi?.Logger?.Notification("[WaypointBeacon] Opened Add Waypoint via vanilla hotkey direct invoke {0}.{1}", hk.GetType().Name, m.Name);
                            return true;
                        }
                        catch { }
                    }
                }

                // 2) Try delegate fields/properties.
                var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                foreach (var f in hk.GetType().GetFields(flags))
                {
                    if (!typeof(Delegate).IsAssignableFrom(f.FieldType)) continue;
                    if (!f.Name.ToLowerInvariant().Contains("handler")) continue;
                    var del = f.GetValue(hk) as Delegate;
                    if (TryInvokeDelegateForHotkey(del, comb))
                    {
                        capi?.Logger?.Notification("[WaypointBeacon] Opened Add Waypoint via vanilla hotkey delegate field {0}", f.Name);
                        return true;
                    }
                }

                foreach (var pr in hk.GetType().GetProperties(flags))
                {
                    if (!pr.CanRead) continue;
                    if (!typeof(Delegate).IsAssignableFrom(pr.PropertyType)) continue;
                    if (!pr.Name.ToLowerInvariant().Contains("handler")) continue;
                    var del = pr.GetValue(hk) as Delegate;
                    if (TryInvokeDelegateForHotkey(del, comb))
                    {
                        capi?.Logger?.Notification("[WaypointBeacon] Opened Add Waypoint via vanilla hotkey delegate property {0}", pr.Name);
                        return true;
                    }
                }
            }
            catch (Exception e)
            {
                capi?.Logger?.Debug("[WaypointBeacon] Vanilla Add Waypoint hotkey invoke failed: {0}", e);
            }

            return false;
        }

        private object FindVanillaAddWaypointHotkey(object input)
        {
            try
            {
                var t = input.GetType();
                var getByCode = t.GetMethod("GetHotKeyByCode", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

                string[] codes = { "addwaypoint", "add-waypoint", "addWaypoint", "worldmap-addwaypoint", "hotkey-addwaypoint" };
                if (getByCode != null)
                {
                    foreach (var code in codes)
                    {
                        try
                        {
                            var hk = getByCode.Invoke(input, new object[] { code });
                            if (hk != null) return hk;
                        }
                        catch { }
                    }
                }

                object hotkeysObj =
                    TryGetMember(input, "HotKeys") ??
                    TryGetMember(input, "hotKeys") ??
                    TryGetMember(input, "Hotkeys") ??
                    TryGetMember(input, "hotkeys");

                if (hotkeysObj is System.Collections.IDictionary dict)
                {
                    foreach (System.Collections.DictionaryEntry de in dict)
                    {
                        if (de.Value == null) continue;
                        if (IsLikelyAddWaypointHotkey(de.Value)) return de.Value;
                    }
                }
                else if (hotkeysObj is System.Collections.IEnumerable en)
                {
                    foreach (var item in en)
                    {
                        if (item == null) continue;
                        if (IsLikelyAddWaypointHotkey(item)) return item;
                    }
                }
            }
            catch { }

            return null;
        }

        private bool IsLikelyAddWaypointHotkey(object hk)
        {
            if (hk == null) return false;
            string code = (TryGetMember(hk, "Code") ?? TryGetMember(hk, "code") ?? "").ToString();
            string name = (TryGetMember(hk, "Name") ?? TryGetMember(hk, "name") ?? TryGetMember(hk, "Description") ?? "").ToString();
            string probe = (code + " " + name).ToLowerInvariant();

            if (probe.Contains("waypoint") && probe.Contains("add"))
            {
                if (probe.Contains("waypointbeacon")) return false;
                return true;
            }

            return false;
        }

        private bool TryInvokeDelegateForHotkey(Delegate del, KeyCombination comb)
        {
            if (del == null) return false;
            try
            {
                var ps = del.Method.GetParameters();
                object[] args = new object[ps.Length];
                for (int i = 0; i < ps.Length; i++)
                {
                    var pt = ps[i].ParameterType;
                    if (typeof(KeyCombination).IsAssignableFrom(pt)) args[i] = comb;
                    else if (typeof(ICoreClientAPI).IsAssignableFrom(pt)) args[i] = capi;
                    else if (typeof(ICoreAPI).IsAssignableFrom(pt)) args[i] = capi;
                    else if (pt.IsValueType) args[i] = Activator.CreateInstance(pt);
                    else args[i] = null;
                }

                object ret = del.DynamicInvoke(args);
                if (ret is bool b) return b;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private bool TryInvokeKnownAddWaypointAction(object target)
        {
            if (target == null) return false;

            var names = new[]
            {
                "OnAddWaypoint", "OnAddWayPoint", "OnHotkeyAddWaypoint", "OnHotKeyAddWaypoint",
                "OpenAddWaypointDialog", "OpenAddWayPointDialog", "ShowAddWaypointDialog", "ShowAddWayPointDialog",
                "AddWaypoint", "AddWayPoint", "OpenNewWaypointDialog", "ShowNewWaypointDialog"
            };

            foreach (string name in names)
            {
                var method = target.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    .FirstOrDefault(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase));

                if (method == null) continue;
                if (!IsSafeAddWaypointMethod(method)) continue;
                if (TryInvokeAddWaypointMethod(target, method))
                {
                    capi?.Logger?.Notification("[WaypointBeacon] Opened Add Waypoint dialog via {0}.{1}", target.GetType().Name, method.Name);
                    return true;
                }
            }

            return false;
        }

        private bool IsSafeAddWaypointMethod(MethodInfo method)
        {
            if (method == null) return false;

            string n = (method.Name ?? "").ToLowerInvariant();
            if (n.StartsWith("oncmd")) return false;
            if (n.Contains("temporary")) return false;

            var ps = method.GetParameters();
            foreach (var p in ps)
            {
                var pt = p.ParameterType;
                string ptn = pt.FullName ?? pt.Name ?? "";

                if (typeof(IServerPlayer).IsAssignableFrom(pt)) return false;
                if (ptn.IndexOf("ServerPlayer", StringComparison.OrdinalIgnoreCase) >= 0) return false;
                if (ptn.IndexOf("TextCommandCallingArgs", StringComparison.OrdinalIgnoreCase) >= 0) return false;
                if (ptn.IndexOf("Waypoint", StringComparison.OrdinalIgnoreCase) >= 0 && !ptn.Contains("WaypointMapLayer")) return false;
            }

            return true;
        }

        private bool TryInvokeAddWaypointMethod(object target, MethodInfo method)
        {
            try
            {
                var mapManager = capi?.ModLoader?.GetModSystem<WorldMapManager>();
                object waypointLayer = GetWaypointMapLayerObject(mapManager);
                var ps = method.GetParameters();
                object[] args = new object[ps.Length];

                for (int i = 0; i < ps.Length; i++)
                {
                    var pt = ps[i].ParameterType;

                    if (typeof(KeyCombination).IsAssignableFrom(pt)) args[i] = CreateDirectAddWaypointKeyCombination();
                    else if (typeof(ICoreClientAPI).IsAssignableFrom(pt)) args[i] = capi;
                    else if (typeof(ICoreAPI).IsAssignableFrom(pt)) args[i] = capi;
                    else if (typeof(WorldMapManager).IsAssignableFrom(pt)) args[i] = mapManager;
                    else if (typeof(IWorldAccessor).IsAssignableFrom(pt)) args[i] = capi?.World;
                    else if (typeof(IPlayer).IsAssignableFrom(pt)) args[i] = capi?.World?.Player;
                    else if (waypointLayer != null && pt.IsInstanceOfType(waypointLayer)) args[i] = waypointLayer;
                    else if (pt.IsValueType) args[i] = Activator.CreateInstance(pt);
                    else if (pt == typeof(string)) args[i] = string.Empty;
                    else args[i] = null;
                }

                object ret = method.Invoke(target, args);
                if (ret is bool b) return b;
                return true;
            }
            catch (Exception e)
            {
                capi?.Logger?.Debug("[WaypointBeacon] Add Waypoint invoke failed on {0}.{1}: {2}", target?.GetType()?.Name, method?.Name, e.InnerException ?? e);
                return false;
            }
        }

        private KeyCombination CreateDirectAddWaypointKeyCombination()
        {
            try
            {
                var comb = new KeyCombination();
                comb.KeyCode = (int)GlKeys.B;
                return comb;
            }
            catch
            {
                return null;
            }
        }

        private object GetWaypointMapLayerObject(WorldMapManager mapManager)
        {
            return mapManager?.MapLayers?.FirstOrDefault(l =>
                l != null && l.GetType().Name.IndexOf("WaypointMapLayer", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private bool TryInvokeAddWaypointOpenMethod(object target)
        {
            if (target == null) return false;

            var t = target.GetType();
            var methods = t.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(m =>
                {
                    string n = m.Name.ToLowerInvariant();
                    bool isVanillaType = (t.FullName ?? "").StartsWith("Vintagestory", StringComparison.OrdinalIgnoreCase);
                    bool waypointAction = n.Contains("way") && n.Contains("point") && (n.Contains("add") || n.Contains("new") || n.Contains("create"));
                    bool vanillaMarkerAction = isVanillaType && n.Contains("marker") && (n.Contains("add") || n.Contains("new") || n.Contains("create"));
                    return waypointAction || vanillaMarkerAction;
                })
                .Where(IsSafeAddWaypointMethod)
                .OrderBy(m => m.GetParameters().Length)
                .ToArray();

            foreach (var m in methods)
            {
                if (!TryInvokeAddWaypointMethod(target, m)) continue;

                capi?.Logger?.Notification("[WaypointBeacon] Opened Add Waypoint dialog via {0}.{1}", t.Name, m.Name);
                return true;
            }

            return false;
        }

        private bool OnToggleBeacons(KeyCombination comb)
        {
            try
            {
                capi?.Logger?.Notification("[WaypointBeacon] Beacon Manager hotkey pressed ({0})", comb?.ToString() ?? "unknown");

                // Reliable toggle: don't use TryClose() as an "is open" test.
                // We track open/closed via the dialog's OnGuiOpened/OnGuiClosed callbacks.
                if (beaconManagerIsOpen && beaconManagerDialog != null)
                {
                    beaconManagerDialog.TryClose(); // OnGuiClosed will clear references
                    return true;
                }

                // Ensure any stale instance is discarded
                beaconManagerDialog = null;
                beaconManagerIsOpen = false;

                beaconManagerDialog = new GuiDialogBeaconManagerSettings(
                    capi,
                    this,
                    onClosed: () =>
                    {
                        beaconManagerIsOpen = false;
                        beaconManagerDialog = null;
                    },
                    onOpened: () =>
                    {
                        beaconManagerIsOpen = true;
                    }
                );

                beaconManagerDialog.TryOpen();
            }
            catch (Exception e)
            {
                capi?.Logger?.Error("[WaypointBeacon] Failed to toggle Beacon Manager: {0}", e);
            }

            return true;
        }

        public class WaypointRow
        {
            public int Id;
            public string Name;

            public bool VanillaPinned;   // what the map says (vanilla)
            public bool BeaconOn;        // mod's independent beacon toggle (persisted)

            public object RawWaypointObject;
        }

        public List<WaypointRow> GetWaypointsSnapshot(string searchFilter)
        {
            string f = (searchFilter ?? "").Trim();
            bool useFilter = f.Length > 0;
            string fLower = f.ToLowerInvariant();


            var list = new List<WaypointRow>();

            foreach (var wp in EnumerateWaypoints())
            {
                // name
                string name = TryGetString(wp, "Title", "title", "Name", "name", "Text", "text") ?? "(unnamed)";

                if (useFilter && !name.ToLowerInvariant().Contains(fLower)) continue;

                // position (needed for stable id fallback)
                if (!TryGetWaypointPos(wp, out double x, out double y, out double z))
                {
                    x = y = z = 0;
                }

                // id (real if available, otherwise stable fallback)
                int id = GetStableWaypointId(wp, x, y, z, name);

                // pinned (vanilla) + beacon toggle (mod)
                bool vanillaPinned;
                if (!TryGetBool(wp, out vanillaPinned, "Pinned", "pinned", "IsPinned", "isPinned")) vanillaPinned = false;

                // Seed an override once per waypoint so vanilla pin changes won't change beacon state later
                string bKey = MakePinKey(x, y, z, name);

                // Detect waypoints that appear after we've established the initial set (covers auto-created waypoints by other mods)
                bool isNewThisSession = false;
                if (!seenWaypointKeysInitialized)
                {
                    seenWaypointKeys.Add(bKey);
                }
                else
                {
                    // HashSet.Add returns true if it was not already present
                    isNewThisSession = seenWaypointKeys.Add(bKey);
                }

                if (!beaconOverrides.ContainsKey(bKey))
                {
                    bool seedOn = false;

                    // Only apply the default to waypoints that are newly created after initial snapshot.
                    if (seenWaypointKeysInitialized && isNewThisSession && DefaultNewWaypointBeaconOn)
                    {
                        seedOn = true;

                        // Persist to server so the beacon state survives reloads (and works in multiplayer)
                        try
                        {
                            if (clientChannel?.Connected == true)
                            {
                                int nid = GetStableWaypointId(wp, x, y, z, name);
                                clientChannel.SendPacket(new WbSetPinnedPacket
                                {
                                    WaypointId = nid,
                                    Pinned = true,
                                    X = x,
                                    Y = y,
                                    Z = z,
                                    Title = name
                                });
                            }
                        }
                        catch { /* best-effort */ }
                    }

                    beaconOverrides[bKey] = seedOn;
                }

                bool beaconOn = beaconOverrides[bKey];
                list.Add(new WaypointRow
                {
                    Id = id,
                    Name = name,
                    VanillaPinned = vanillaPinned,
                    BeaconOn = beaconOn,
                    RawWaypointObject = wp
                });
            }

            return list;
        }

        public bool SetWaypointPinnedById(int waypointId, bool pinned)
        {
            try
            {
                if (!TryGetWaypointList(out object layer, out System.Collections.IList list))
                {
                    capi.Logger.Warning("[WaypointBeacon] TryGetWaypointList failed (no layer/list).");
                    return false;
                }

                for (int i = 0; i < list.Count; i++)
                {
                    object wp = list[i];
                    if (wp == null) continue;

                    string name = TryGetString(wp, "Title", "title", "Name", "name", "Text", "text") ?? "(unnamed)";
                    if (!TryGetWaypointPos(wp, out double x, out double y, out double z))
                    {
                        x = y = z = 0;
                    }

                    int id = GetStableWaypointId(wp, x, y, z, name);
                    if (id != waypointId) continue;

                    // IMPORTANT: wp might be a struct (value type). This is a boxed copy.
                    // We must modify the boxed copy THEN write it back into the list slot.
                    // IMPORTANT: we do NOT change the vanilla waypoint here.
                    // We only change our beacon override and persist it to the server.
                    object boxed = wp; // boxed copy (safe if wp is a struct)

                    TryGetWaypointPos(boxed, out double px, out double py, out double pz);
                    string title = TryGetString(boxed, "Title", "title", "Name", "name", "Text", "text") ?? "";

                    string oKey = MakePinKey(px, py, pz, title);
                    beaconOverrides[oKey] = pinned;

                    var pkt = new WbSetPinnedPacket
                    {
                        WaypointId = waypointId,
                        Pinned = pinned,
                        X = px,
                        Y = py,
                        Z = pz,
                        Title = title
                    };

                    if (clientChannel?.Connected == true)
                    {
                        clientChannel.SendPacket(pkt);
                    }
                    else
                    {
                        capi.Logger.Warning("[WaypointBeacon] Beacon override updated, but network channel not connected yet. (No persistence)");
                    }

                    RefreshBeaconsNow();

                    capi.Logger.Notification("[WaypointBeacon] Sent Beacon request - Updated locally {0} -> {1}", waypointId, pinned);
                    return true;
                }

                capi.Logger.Warning("[WaypointBeacon] Waypoint id {0} not found in list.", waypointId);
                return false;
            }
            catch (Exception e)
            {
                capi.Logger.Error("[WaypointBeacon] SetWaypointPinnedById failed: {0}", e);
                return false;
            }
        }

        /// <summary>
        /// Sets the beacon ON/OFF override for every currently known waypoint, and refreshes rendering.
        /// This mirrors what you'd do manually (open Edit on each waypoint and toggle the beacon checkbox),
        /// and also syncs the changes to the server in multiplayer.
        /// </summary>
        public void SetAllWaypointBeacons(bool beaconOn)
        {
            if (capi == null) return;

            int changed = 0;

            try
            {
                foreach (var wp in EnumerateWaypoints())
                {
                    if (wp == null) continue;

                    string title = TryGetString(wp, "Title", "title", "Name", "name", "Text", "text") ?? "";
                    if (!TryGetWaypointPos(wp, out double x, out double y, out double z)) continue;

                    int stableId = GetStableWaypointId(wp, x, y, z, title);
                    string pinKey = MakePinKey(x, y, z, title);

                    beaconOverrides[pinKey] = beaconOn;

                    // Sync to server (if connected) so other players / server persistence stays correct.
                    if (clientChannel?.Connected == true)
                    {
                        clientChannel.SendPacket(new WbSetPinnedPacket
                        {
                            WaypointId = stableId,
                            Pinned = beaconOn,
                            X = x,
                            Y = y,
                            Z = z,
                            Title = title
                        });
                    }

                    changed++;
                }
            }
            catch (Exception e)
            {
                capi.Logger.Error(e);
                capi.Logger.Warning("[WaypointBeacon] Failed SetAllWaypointBeacons(" + beaconOn + "): " + e);
            }

            RefreshBeaconsNow();
            capi.Logger.Notification("[WaypointBeacon] Set beacon=" + (beaconOn ? "ON" : "OFF") + " for " + changed + " waypoints.");
        }



        private void SetPinnedOnBoxedWaypoint(ref object rawWaypointBox, bool pinned)
        {
            if (rawWaypointBox == null) return;

            var t = rawWaypointBox.GetType();

            // properties first
            foreach (var propName in new[] { "Pinned", "pinned", "IsPinned", "isPinned" })
            {
                var p = t.GetProperty(propName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (p != null && p.CanWrite && p.PropertyType == typeof(bool))
                {
                    p.SetValue(rawWaypointBox, pinned);
                    return;
                }
            }

            // fields
            foreach (var fieldName in new[] { "Pinned", "pinned", "isPinned" })
            {
                var f = t.GetField(fieldName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (f != null && f.FieldType == typeof(bool))
                {
                    f.SetValue(rawWaypointBox, pinned);
                    return;
                }
            }
        }

        private bool TryGetWaypointList(out object layer, out System.Collections.IList list)
        {
            layer = null;
            list = null;

            var mapManager = capi.ModLoader.GetModSystem<WorldMapManager>();
            if (mapManager?.MapLayers == null) return false;

            layer = mapManager.MapLayers.FirstOrDefault(l =>
                l != null && l.GetType().Name.IndexOf("WaypointMapLayer", StringComparison.OrdinalIgnoreCase) >= 0);

            if (layer == null) return false;

            object listObj =
                TryGetMember(layer, "ownWaypoints") ??
                TryGetMember(layer, "OwnWaypoints") ??
                TryGetMember(layer, "waypoints") ??
                TryGetMember(layer, "Waypoints");

            if (listObj is System.Collections.IList ilist)
            {
                list = ilist;
                return true;
            }

            return false;
        }


        /// <summary>
        /// Tries to resolve the game's loaded icon texture for a waypoint icon key (e.g. "circle", "home", etc.).
        /// Uses the current WaypointMapLayer's texturesByIcon dictionary.
        /// </summary>
        private bool TryGetWaypointIconTexture(string iconCode, out LoadedTexture tex)
        {
            tex = null;
            if (string.IsNullOrEmpty(iconCode)) return false;

            if (!TryGetWaypointList(out object layer, out System.Collections.IList _)) return false;

            // WaypointMapLayer stores icon textures here
            var dictObj =
                TryGetMember(layer, "texturesByIcon") ??
                TryGetMember(layer, "TexturesByIcon") ??
                TryGetMember(layer, "textures") ??
                TryGetMember(layer, "Textures");

            if (dictObj is Dictionary<string, LoadedTexture> dict)
            {
                if (!dict.TryGetValue(iconCode, out tex))
                {
                    dict.TryGetValue("circle", out tex);
                }
                return tex != null;
            }

            // Some versions expose IDictionary
            if (dictObj is System.Collections.IDictionary idict)
            {
                object val = null;
                if (idict.Contains(iconCode)) val = idict[iconCode];
                else if (idict.Contains("circle")) val = idict["circle"];

                tex = val as LoadedTexture;
                return tex != null;
            }

            return false;
        }

        private void NotifyWaypointLayerChanged(object layer)
        {
            // Best-effort “poke” so other systems/UI stop overwriting.
            // We don’t know exact method names across VS versions, so we try a few.
            TryInvoke(layer, "OnWaypointsChanged");
            TryInvoke(layer, "MarkDirty");
            TryInvoke(layer, "SetDirty");
            TryInvoke(layer, "RebuildCache");
            TryInvoke(layer, "RebuildWaypoints");

            // Also poke WorldMapManager if it has save/dirty methods
            var mapManager = capi.ModLoader.GetModSystem<WorldMapManager>();
            if (mapManager != null)
            {
                TryInvoke(mapManager, "MarkDirty");
                TryInvoke(mapManager, "SaveMapData");
                TryInvoke(mapManager, "Save");
            }
        }

        private void TryInvoke(object obj, string methodName)
        {
            if (obj == null) return;
            try
            {
                var t = obj.GetType();
                var m = t.GetMethod(methodName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (m != null && m.GetParameters().Length == 0)
                {
                    m.Invoke(obj, null);
                }
            }
            catch { /* ignore */ }
        }

        public void RefreshBeaconsNow()
        {
            // call your existing refresh routine (whatever it's named in your file)
            // If your method name is RefreshBeacons() and it is private, either:
            // 1) make it internal/public, OR
            // 2) duplicate the refresh logic here
            //
            // If your current refresh method is named RefreshBeacons(), just call it:
            RefreshBeacons();
        }

        public override void Dispose()
        {
            WaypointDialogBeaconPatch.Dispose();

            beaconManagerDialog?.TryClose();


            beaconManagerDialog = null;


            base.Dispose();
            if (capi != null)
            {
                if (tickListenerId != 0) capi.Event.UnregisterGameTickListener(tickListenerId);
                labelRenderer?.DisposeAllTextures();
            }
        }

        private EnumRenderStage ChooseBeamStage()
        {
            string[] names = Enum.GetNames(typeof(EnumRenderStage));
            string pick = names.FirstOrDefault(n => n.IndexOf("opaque", StringComparison.OrdinalIgnoreCase) >= 0)
                       ?? names.FirstOrDefault();

            if (pick != null && Enum.TryParse(pick, true, out EnumRenderStage stage))
            {
                return stage;
            }

            return default;
        }

        private void RefreshBeacons()
        {
            try
            {
                visibleBeacons.Clear();

                if (!GlobalBeaconsEnabled) return;

                bool captureInitialSeen = !seenWaypointKeysInitialized;




                var player = capi.World?.Player;
                var ent = player?.Entity;
                if (ent == null) return;

                double px = ent.Pos.X;
                double pz = ent.Pos.Z;

                foreach (var wp in EnumerateWaypoints())
                {
                    if (!TryGetWaypointPos(wp, out double x, out double y, out double z)) continue;

                    // XZ distance only
                    double dx = x - px;
                    double dz = z - pz;
                    double dist = Math.Sqrt(dx * dx + dz * dz);
                    if (dist > MaxRenderDistanceXZ) continue;

                    string name = TryGetString(wp, "Title", "title", "Name", "name", "Text", "text") ?? "Waypoint";
                    string icon = TryGetString(wp, "Icon", "icon") ?? "";
                    bool vanillaPinned;
                    if (!TryGetBool(wp, out vanillaPinned, "Pinned", "pinned", "IsPinned", "isPinned")) vanillaPinned = false;

                    // Beacon is independent from vanilla pinning.
                    // We seed an override once per waypoint (first time we see it) so that later vanilla pin changes
                    // do NOT affect beacon state.
                    string bKey = MakePinKey(x, y, z, name);
                    // If another mod created a new waypoint (without the Add dialog), apply the default beacon setting
                    // once when we first notice it. Manual waypoint creation is handled by our Add dialog onSave patch,
                    // which writes an explicit override and will therefore not be overwritten here.
                    if (captureInitialSeen)
                    {
                        seenWaypointKeys.Add(bKey);
                    }
                    else if (!seenWaypointKeys.Contains(bKey))
                    {
                        seenWaypointKeys.Add(bKey);
                        if (DefaultNewWaypointBeaconOn && !beaconOverrides.ContainsKey(bKey))
                        {
                            SetBeaconOnForWaypointObject(wp, true);
                            // ensure local cache reflects it immediately
                            beaconOverrides[bKey] = true;
                        }
                    }

                    if (!beaconOverrides.ContainsKey(bKey))
                    {
                        beaconOverrides[bKey] = false;
                    }

                    bool beaconOn = beaconOverrides[bKey];
                    if (!beaconOn) continue;
                    int colorInt = TryGetInt(wp, "Color", "color", "ARGB", "argb", "Rgb", "rgb", "RGBA", "rgba")
                                                       ?? unchecked((int)0xFFFFFFFF);

                    int id = GetStableWaypointId(wp, x, y, z, name);

                    Vec4f rgba = ColorIntToRgba(colorInt);
                    if (IsNearBlack(rgba))
                    {
                        rgba = new Vec4f(0.25f, 1f, 1f, 1f);
                    }

                    visibleBeacons.Add(new BeaconInfo
                    {
                        Id = id,
                        X = x,
                        Y = y,
                        Z = z,
                        Name = name,
                        Icon = icon,
                        ColorInt = colorInt,
                        ColorRgba = rgba
                    });
                }
                // After the first successful refresh, any newly discovered waypoint will be treated as 'new' for default-beacon purposes
                if (!seenWaypointKeysInitialized) seenWaypointKeysInitialized = true;

            }
            catch (Exception e)
            {
                capi.Logger.Error("[WaypointBeacon] RefreshBeacons exception: {0}", e);
            }
        }

        public IReadOnlyList<BeaconInfo> GetVisibleBeacons() => visibleBeacons;

        public int GetWorldHeightBlocks()
        {
            int y = capi.World.BlockAccessor.MapSizeY;
            if (y > 0 && y <= 64) y *= 32;
            if (y < 128) y = 256;
            return y;
        }

        // ---- Label generation (single line + CGJ sentinel) ----
        private string BuildLabel(BeaconInfo b)
        {
            string name = b.Name ?? "";
            name = name.Replace('\r', ' ').Replace('\n', ' ');
            if (name.Length == 0) name = "Waypoint";

            int style = LabelStyleMode;
            if (style == 0) return name;

            // Coords are stable; distance is dynamic (handled by periodic cache invalidation in the renderer).
            if (style == 2)
            {
                int halfX = capi.World.BlockAccessor.MapSizeX / 2;
                int halfZ = capi.World.BlockAccessor.MapSizeZ / 2;

                int dx = (int)Math.Floor(b.X) - halfX;
                int dy = (int)Math.Floor(b.Y);
                int dz = (int)Math.Floor(b.Z) - halfZ;

                string coordsLine = $"[{dx}, {dy}, {dz}]"; 
                
                return name + "\n" + coordsLine;
            }

            // style == 1: Label + Distance
            try
            {
                var plr = capi?.World?.Player?.Entity;
                if (plr != null)
                {
                    double dx = b.X - plr.Pos.X;
                    double dy = b.Y - plr.Pos.Y;
                    double dz = b.Z - plr.Pos.Z;
                    double dist = Math.Sqrt(dx * dx + dy * dy + dz * dz);

                    // Quantize to reduce texture churn
                    int meters = (int)Math.Round(dist);
                    int step = meters < 100 ? 1 : (meters < 500 ? 5 : 10);
                    int q = (int)(Math.Round(meters / (double)step) * step);

                    string distStr = q >= 1000 ? (q / 1000d).ToString("0.0") + " km" : q + " m";
                    return name + "\n" + distStr;
                }
            }
            catch { }

            return name;
        }

        public LoadedTexture GetOrCreateLabelTexture(BeaconInfo b, BeaconLabelRenderer cacheOwner, bool outline)
        {
            // IMPORTANT: same exact label for OL and FG so the textures match perfectly
            string label = BuildLabel(b);

            string key = outline ? $"{b.Id}:OL:{label}" : $"{b.Id}:FG:{label}";
            if (cacheOwner.TryGetCached(key, out LoadedTexture tex)) return tex;

            CairoFont font = CreateFont(GetEffectiveLabelFontPx());
            font.Color = outline
                ? new double[] { 0, 0, 0, 1.0 }
                : new double[] { 1, 1, 1, 1.0 };

            EnumTextOrientation orient = label.Contains("\n") ? EnumTextOrientation.Center : EnumTextOrientation.Left;

            LoadedTexture newTex = textUtil.GenTextTexture(label, font, LabelMaxWidthPx, textBg, orient);

            cacheOwner.StoreCached(key, newTex);
            return newTex;
        }

        private CairoFont CreateFont(float px)
        {
            try
            {
                return new CairoFont(px, "Sans", new double[] { 1, 1, 1, 1 });
            }
            catch
            {
                return CairoFont.WhiteSmallText();
            }
        }

        private TextBackground MakeInvisibleTextBg()
        {
            var bg = new TextBackground();

            void Set(string name, object value)
            {
                var t = bg.GetType();
                var p = t.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (p != null && p.CanWrite) { p.SetValue(bg, value); return; }
                var f = t.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (f != null) { f.SetValue(bg, value); }
            }

            // Fully transparent fill + fully transparent border
            Set("FillColor", new double[] { 0, 0, 0, 0.0 });
            Set("BorderColor", new double[] { 0, 0, 0, 0.0 });

            // No padding, no border, no rounding
            Set("Padding", 1);
            Set("BorderWidth", 0);
            Set("Radius", 0);

            return bg;
        }

        // ---------------------------
        // Waypoint enumeration helpers
        // ---------------------------

        private void HandleSetPinnedPacket(ICoreServerAPI sapi, IServerPlayer player, WbSetPinnedPacket pkt)
        {
            sapi.Logger.Notification("[WaypointBeacon] Server got beacon request: id={0} pinned={1} title={2}", pkt.WaypointId, pkt.Pinned, pkt.Title);

            try
            {
                // Persist overrides inside the player's watched attributes (saved with player data)
                string attrKey = PinsAttrKeyPrefix + sapi.World.Seed.ToString();

                var dict = LoadPinsFromPlayer(player, attrKey);
                string key = MakePinKey(pkt.X, pkt.Y, pkt.Z, pkt.Title ?? "");

                dict[key] = pkt.Pinned;
                SavePinsToPlayer(player, attrKey, dict);

                // Sync the authoritative override set back to the same player
                SendPinsToPlayer(sapi, player);
            }
            catch (Exception e)
            {
                sapi.Logger.Error("[WaypointBeacon] Server HandleSetPinnedPacket failed: {0}", e);
            }
        }

        private void HandleCreateWaypointPacket(ICoreServerAPI sapi, IServerPlayer player, WbCreateWaypointPacket pkt)
        {
            try
            {
                if (player == null || pkt == null) { sapi.Logger.Warning("[WaypointBeacon] Server create waypoint request invalid (player/pkt null)"); return; }

                var mapManager = sapi.ModLoader.GetModSystem<WorldMapManager>();
                if (mapManager == null) { sapi.Logger.Warning("[WaypointBeacon] Server create waypoint failed: WorldMapManager not found"); return; }

                object mapData = TryGetServerPlayerMapData(mapManager, player);
                if (mapData == null) { sapi.Logger.Warning("[WaypointBeacon] Server create waypoint failed: mapData not found for player {0}", player?.PlayerUID ?? "null"); return; }

                if (!TryGetWaypointsListFromMapData(mapData, out IList list) || list == null) { sapi.Logger.Warning("[WaypointBeacon] Server create waypoint failed: waypoints list not resolved for player {0}", player?.PlayerUID ?? "null"); return; }

                var wp = new Waypoint();
                double px = pkt.X + 0.5;
                double py = pkt.Y + 0.5;
                double pz = pkt.Z + 0.5;

                TrySetMemberValueStatic(wp, "Position", new Vec3d(px, py, pz));
                TrySetMemberValueStatic(wp, "Pos", new Vec3d(px, py, pz));
                TrySetMemberValueStatic(wp, "X", px);
                TrySetMemberValueStatic(wp, "Y", py);
                TrySetMemberValueStatic(wp, "Z", pz);

                string title = string.IsNullOrWhiteSpace(pkt.Title) ? "New Beacon" : pkt.Title;
                TrySetMemberValueStatic(wp, "Title", title);
                TrySetMemberValueStatic(wp, "Text", title);
                TrySetMemberValueStatic(wp, "Name", title);
                TrySetMemberValueStatic(wp, "Icon", string.IsNullOrWhiteSpace(pkt.Icon) ? "circle" : pkt.Icon);
                TrySetMemberValueStatic(wp, "Symbol", string.IsNullOrWhiteSpace(pkt.Icon) ? "circle" : pkt.Icon);
                TrySetMemberValueStatic(wp, "Color", string.IsNullOrWhiteSpace(pkt.Color) ? "#ffd700" : pkt.Color);

                int maxId = 0;
                foreach (var obj in list)
                {
                    if (obj == null) continue;
                    int id = TryGetInt(obj, "WaypointID", "WaypointId", "WaypointIDInt", "Id", "ID", "id") ?? 0;
                    if (id > maxId) maxId = id;
                }
                int newId = maxId + 1;
                TrySetMemberValueStatic(wp, "WaypointID", newId);
                TrySetMemberValueStatic(wp, "WaypointId", newId);
                TrySetMemberValueStatic(wp, "Id", newId);
                TrySetMemberValueStatic(wp, "OwningPlayerUid", player.PlayerUID);
                TrySetMemberValueStatic(wp, "OwnerUid", player.PlayerUID);
                TrySetMemberValueStatic(wp, "PlayerUid", player.PlayerUID);

                list.Add(wp);

                TryInvokeParameterlessMethodStatic(mapData, "MarkDirty");
                TryInvokeParameterlessMethodStatic(mapManager, "MarkDirty");
                TryInvokeParameterlessMethodStatic(mapData, "Save");
                TryInvokeParameterlessMethodStatic(mapManager, "Save");

                sapi.Logger.Notification("[WaypointBeacon] Server created waypoint id={0} for {1} at {2},{3},{4}", newId, player.PlayerUID, pkt.X, pkt.Y, pkt.Z);
            }
            catch (Exception e)
            {
                sapi.Logger.Warning("[WaypointBeacon] Server create waypoint failed: {0}", e);
            }
        }

        private static bool TrySetMemberValueStatic(object obj, string memberName, object value)
        {
            if (obj == null || string.IsNullOrEmpty(memberName)) return false;

            var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            Type t = obj.GetType();

            try
            {
                var prop = t.GetProperty(memberName, flags);
                if (prop != null && prop.CanWrite)
                {
                    object converted = ConvertValueForTypeStatic(value, prop.PropertyType);
                    if (converted != null || !prop.PropertyType.IsValueType)
                    {
                        prop.SetValue(obj, converted);
                        return true;
                    }
                }

                var field = t.GetField(memberName, flags);
                if (field != null)
                {
                    object converted = ConvertValueForTypeStatic(value, field.FieldType);
                    if (converted != null || !field.FieldType.IsValueType)
                    {
                        field.SetValue(obj, converted);
                        return true;
                    }
                }
            }
            catch { }

            return false;
        }

        private static object ConvertValueForTypeStatic(object value, Type targetType)
        {
            if (targetType == null) return value;
            if (value == null) return targetType.IsValueType ? Activator.CreateInstance(targetType) : null;
            if (targetType.IsInstanceOfType(value)) return value;

            try
            {
                if (targetType.IsEnum)
                {
                    if (value is string s) return Enum.Parse(targetType, s, true);
                    return Enum.ToObject(targetType, value);
                }

                if (targetType == typeof(string)) return value.ToString();
                return Convert.ChangeType(value, targetType);
            }
            catch
            {
                return null;
            }
        }

        private static void TryInvokeParameterlessMethodStatic(object target, string methodName)
        {
            if (target == null || string.IsNullOrEmpty(methodName)) return;
            try
            {
                var method = target.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
                method?.Invoke(target, null);
            }
            catch { }
        }

        // =========================
        // Pin override helpers (persistence + anti-snapback)
        // =========================

        private static string MakePinKey(double x, double y, double z, string title)
        {
            // Round to block coords to avoid float noise
            int xi = (int)Math.Round(x);
            int yi = (int)Math.Round(y);
            int zi = (int)Math.Round(z);
            title = title ?? "";
            return $"{xi},{yi},{zi}|{title}";
        }

        private Dictionary<string, bool> LoadPinsFromPlayer(IServerPlayer player, string attrKey)
        {
            var dict = new Dictionary<string, bool>();

            try
            {
                string raw = player?.Entity?.WatchedAttributes?.GetString(attrKey, null);
                if (string.IsNullOrEmpty(raw)) return dict;

                // Format: one entry per line => "<key>\t<0|1>"
                var lines = raw.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var line in lines)
                {
                    int tab = line.LastIndexOf('\t');
                    if (tab <= 0) continue;

                    string k = line.Substring(0, tab);
                    string v = line.Substring(tab + 1).Trim();

                    if (string.IsNullOrEmpty(k)) continue;

                    bool b = v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase);
                    dict[k] = b;
                }
            }
            catch
            {
                // ignore and return empty
            }

            return dict;
        }

        private void SavePinsToPlayer(IServerPlayer player, string attrKey, Dictionary<string, bool> dict)
        {
            try
            {
                if (player?.Entity?.WatchedAttributes == null) return;

                var sb = new StringBuilder();
                foreach (var kv in dict)
                {
                    if (string.IsNullOrEmpty(kv.Key)) continue;
                    sb.Append(kv.Key);
                    sb.Append('\t');
                    sb.Append(kv.Value ? "1" : "0");
                    sb.Append('\n');
                }

                player.Entity.WatchedAttributes.SetString(attrKey, sb.ToString());

                // Mark dirty if the method exists (not required for our explicit network sync,
                // but helps the game know the attribute changed)
                try
                {
                    var wa = player.Entity.WatchedAttributes;
                    var m = wa.GetType().GetMethod("MarkPathDirty", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(string) }, null);
                    m?.Invoke(wa, new object[] { attrKey });
                }
                catch { }
            }
            catch { }
        }

        private void SendPinsToPlayer(ICoreServerAPI sapi, IServerPlayer player)
        {
            try
            {
                string attrKey = PinsAttrKeyPrefix + sapi.World.Seed.ToString();
                var dict = LoadPinsFromPlayer(player, attrKey);

                var pkt = new WbPinsSyncPacket();
                foreach (var kv in dict)
                {
                    pkt.Keys.Add(kv.Key);
                    pkt.Pinned.Add(kv.Value);
                }

                serverChannel?.SendPacket(pkt, player);
            }
            catch (Exception e)
            {
                sapi?.Logger?.Warning("[WaypointBeacon] SendPinsToPlayer failed: {0}", e.Message);
            }
        }

        private void OnPinsSyncPacket(WbPinsSyncPacket pkt)
        {
            try
            {
                beaconOverrides.Clear();

                int n = Math.Min(pkt?.Keys?.Count ?? 0, pkt?.Pinned?.Count ?? 0);
                for (int i = 0; i < n; i++)
                {
                    string k = pkt.Keys[i];
                    if (string.IsNullOrEmpty(k)) continue;
                    beaconOverrides[k] = pkt.Pinned[i];
                }

                RefreshBeaconsNow();
            }
            catch (Exception e)
            {
                capi?.Logger?.Warning("[WaypointBeacon] OnPinsSyncPacket failed: {0}", e.Message);
            }
        }

        private void ApplyPinOverridesToLiveWaypoints()
        {
            // No longer used: we do not override vanilla waypoint data.
        }



        private object TryGetServerPlayerMapData(WorldMapManager mapManager, IServerPlayer player)
        {
            // Try common field names first
            object dictObj =
                TryGetMember(mapManager, "playerMapData") ??
                TryGetMember(mapManager, "playerMapDatas") ??
                TryGetMember(mapManager, "mapDataByPlayer") ??
                TryGetMember(mapManager, "mapDataByUid");

            if (dictObj is System.Collections.IDictionary dict)
            {
                // keys might be uid or player reference
                if (dict.Contains(player.PlayerUID)) return dict[player.PlayerUID];
                if (dict.Contains(player)) return dict[player];
            }

            // Try common method names
            var t = mapManager.GetType();
            var methods = t.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

            foreach (var m in methods)
            {
                string n = m.Name.ToLowerInvariant();
                if (!n.Contains("map") || !n.Contains("data")) continue;

                var ps = m.GetParameters();
                if (ps.Length == 1)
                {
                    if (ps[0].ParameterType == typeof(string))
                    {
                        return m.Invoke(mapManager, new object[] { player.PlayerUID });
                    }
                    if (typeof(IServerPlayer).IsAssignableFrom(ps[0].ParameterType))
                    {
                        return m.Invoke(mapManager, new object[] { player });
                    }
                }
            }

            return null;
        }

        private bool TryGetWaypointsListFromMapData(object mapData, out System.Collections.IList list)
        {
            list = null;

            object listObj =
                TryGetMember(mapData, "ownWaypoints") ??
                TryGetMember(mapData, "OwnWaypoints") ??
                TryGetMember(mapData, "waypoints") ??
                TryGetMember(mapData, "Waypoints");

            if (listObj is System.Collections.IList ilist)
            {
                list = ilist;
                return true;
            }

            return false;
        }

        private void TryInvokeCompatible(object obj, object arg1, string nameContains)
        {
            if (obj == null) return;

            var t = obj.GetType();
            var methods = t.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

            foreach (var m in methods)
            {
                string n = m.Name.ToLowerInvariant();
                if (!n.Contains(nameContains.ToLowerInvariant())) continue;

                var ps = m.GetParameters();
                if (ps.Length != 1) continue;
                if (!ps[0].ParameterType.IsInstanceOfType(arg1)) continue;

                try { m.Invoke(obj, new[] { arg1 }); } catch { }
            }
        }

        private bool TrySetPinnedViaEngine(int waypointId, bool pinned)
        {
            var mapManager = capi.ModLoader.GetModSystem<WorldMapManager>();
            if (mapManager?.MapLayers == null) return false;

            object layer = mapManager.MapLayers.FirstOrDefault(l =>
                l != null && l.GetType().Name.IndexOf("WaypointMapLayer", StringComparison.OrdinalIgnoreCase) >= 0);

            // 1) Prefer methods on the layer itself
            if (TryCallPinMethod(layer, waypointId, pinned)) return true;

            // 2) Some versions put it on WorldMapManager
            if (TryCallPinMethod(mapManager, waypointId, pinned)) return true;

            return false;
        }

        private bool TryCallPinMethod(object target, int waypointId, bool pinned)
        {
            if (target == null) return false;

            var t = target.GetType();
            var methods = t.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

            foreach (var m in methods)
            {
                string n = m.Name.ToLowerInvariant();
                if (!n.Contains("pin")) continue;
                if (!n.Contains("way")) continue;   // waypoint/waypoints

                var ps = m.GetParameters();

                // (int,bool) or (long,bool)
                if (ps.Length == 2 && ps[1].ParameterType == typeof(bool))
                {
                    if (ps[0].ParameterType == typeof(int))
                    {
                        m.Invoke(target, new object[] { waypointId, pinned });
                        return true;
                    }
                    if (ps[0].ParameterType == typeof(long))
                    {
                        m.Invoke(target, new object[] { (long)waypointId, pinned });
                        return true;
                    }
                }

                // toggle style: (int) / (long)
                if (ps.Length == 1 && (n.Contains("toggle") || n.Contains("switch")))
                {
                    if (ps[0].ParameterType == typeof(int))
                    {
                        m.Invoke(target, new object[] { waypointId });
                        return true;
                    }
                    if (ps[0].ParameterType == typeof(long))
                    {
                        m.Invoke(target, new object[] { (long)waypointId });
                        return true;
                    }
                }
            }

            return false;
        }

        private int GetStableWaypointId(object wp, double x, double y, double z, string name)
        {
            int? id = TryGetInt(wp, "WaypointID", "WaypointId", "WaypointIDInt", "Id", "ID", "id");
            // If reflection fails (or returns 0), fall back to a deterministic hash from position+name
            if (!id.HasValue || id.Value == 0)
            {
                return MakeFallbackId(x, y, z, name);
            }
            return id.Value;
        }

        private IEnumerable<object> EnumerateWaypoints()
        {
            var mapManager = capi.ModLoader.GetModSystem<WorldMapManager>();
            if (mapManager?.MapLayers == null) yield break;

            var layer = mapManager.MapLayers.FirstOrDefault(l => l != null && l.GetType().Name.IndexOf("WaypointMapLayer", StringComparison.OrdinalIgnoreCase) >= 0);
            if (layer == null) yield break;

            object listObj =
                TryGetMember(layer, "ownWaypoints") ??
                TryGetMember(layer, "OwnWaypoints") ??
                TryGetMember(layer, "waypoints") ??
                TryGetMember(layer, "Waypoints");

            if (listObj is IEnumerable enumerable)
            {
                foreach (var wp in enumerable)
                {
                    if (wp != null) yield return wp;
                }
            }
        }

        private static object TryGetMember(object obj, string name)
        {
            var t = obj.GetType();
            var f = t.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (f != null) return f.GetValue(obj);

            var p = t.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (p != null) return p.GetValue(obj);

            return null;
        }

        private static bool TryGetWaypointPos(object wp, out double x, out double y, out double z)
        {
            x = y = z = 0;

            object posObj =
                TryGetMember(wp, "Position") ??
                TryGetMember(wp, "Pos") ??
                TryGetMember(wp, "position") ??
                TryGetMember(wp, "pos");

            if (posObj is Vec3d v3d)
            {
                x = v3d.X; y = v3d.Y; z = v3d.Z;
                return true;
            }

            double? xx = TryGetDouble(wp, "X", "x");
            double? yy = TryGetDouble(wp, "Y", "y");
            double? zz = TryGetDouble(wp, "Z", "z");

            if (xx.HasValue && yy.HasValue && zz.HasValue)
            {
                x = xx.Value; y = yy.Value; z = zz.Value;
                return true;
            }

            return false;
        }

        private static bool TryGetBool(object obj, out bool val, params string[] names)
        {
            val = false;
            foreach (var n in names)
            {
                object m = TryGetMember(obj, n);
                if (m is bool b) { val = b; return true; }
                if (m is int i) { val = i != 0; return true; }
            }
            return false;
        }

        private static string TryGetString(object obj, params string[] names)
        {
            foreach (var n in names)
            {
                object m = TryGetMember(obj, n);
                if (m is string s && !string.IsNullOrEmpty(s)) return s;
            }
            return null;
        }

        private static int? TryGetInt(object obj, params string[] names)
        {
            foreach (var n in names)
            {
                object m = TryGetMember(obj, n);
                if (m is int i) return i;
                if (m is long l) return (int)l;
            }
            return null;
        }

        private static double? TryGetDouble(object obj, params string[] names)
        {
            foreach (var n in names)
            {
                object m = TryGetMember(obj, n);
                if (m is double d) return d;
                if (m is float f) return f;
                if (m is int i) return i;
                if (m is long l) return l;
            }
            return null;
        }

        private static int MakeFallbackId(double x, double y, double z, string name)
        {
            unchecked
            {
                int h = 17;
                h = h * 31 + (int)Math.Round(x);
                h = h * 31 + (int)Math.Round(y);
                h = h * 31 + (int)Math.Round(z);
                h = h * 31 + (name?.GetHashCode() ?? 0);
                return h;
            }
        }

        private static Vec4f ColorIntToRgba(int colorInt)
        {
            byte a = (byte)((colorInt >> 24) & 0xFF);
            byte r = (byte)((colorInt >> 16) & 0xFF);
            byte g = (byte)((colorInt >> 8) & 0xFF);
            byte b = (byte)(colorInt & 0xFF);

            if (SwapRedBlue)
            {
                byte tmp = r; r = b; b = tmp;
            }

            return new Vec4f(r / 255f, g / 255f, b / 255f, a / 255f);
        }

        private static int ColorIntToRenderLineRgbaInt(int colorInt)
        {
            byte a = (byte)((colorInt >> 24) & 0xFF);
            byte r = (byte)((colorInt >> 16) & 0xFF);
            byte g = (byte)((colorInt >> 8) & 0xFF);
            byte b = (byte)(colorInt & 0xFF);

            if (SwapRedBlue)
            {
                byte tmp = r; r = b; b = tmp;
            }

            // AABBGGRR
            return (a << 24) | (b << 16) | (g << 8) | r;
        }

        private static bool IsNearBlack(Vec4f rgba)
        {
            return rgba.X < 0.05f && rgba.Y < 0.05f && rgba.Z < 0.05f;
        }

        public class BeaconInfo
        {
            public int Id;
            public double X, Y, Z;
            public string Name;
            public string Icon;
            public int ColorInt;
            public Vec4f ColorRgba;
        }

        // ---------------------------
        // Beam renderer (unchanged)
        // ---------------------------

        public class BeaconBeamRenderer : IRenderer
        {
            private readonly ICoreClientAPI capi;
            private readonly WaypointBeaconModSystem mod;

            public BeaconBeamRenderer(ICoreClientAPI capi, WaypointBeaconModSystem mod)
            {
                this.capi = capi;
                this.mod = mod;
            }

            public double RenderOrder => 0.5;
            public int RenderRange => (mod?.MaxRenderDistanceXZ ?? DefaultMaxRenderDistanceXZ) + 64;

            public void OnRenderFrame(float deltaTime, EnumRenderStage stage)
            {
                if (!(mod?.BeamsEnabled ?? true)) return;

                var beacons = mod.GetVisibleBeacons();
                if (beacons == null || beacons.Count == 0) return;

                int worldTopY = mod.GetWorldHeightBlocks() - 1;

                Vec3d camPos = capi.World.Player.Entity.CameraPos;
                BlockPos origin = new BlockPos((int)camPos.X, (int)camPos.Y, (int)camPos.Z);

                foreach (var b in beacons)
                {

                    double x = Math.Floor(b.X) + 0.5;
                    double z = Math.Floor(b.Z) + 0.5;

                    double y0 = Math.Floor(b.Y);
                    if (y0 < 1) y0 = 1;
                    if (y0 > worldTopY - 2) y0 = worldTopY - 2;

                    Vec3d start = new Vec3d(x, y0, z);
                    Vec3d end = new Vec3d(x, worldTopY, z);


                    float fadeAlpha = mod?.ComputeNearFadeAlpha(camPos, x, Math.Floor(b.Y) + 0.5, z) ?? 1f;
                    if (fadeAlpha <= 0.01f) continue;
                    int rgbaInt = ColorIntToRenderLineRgbaInt(b.ColorInt);
                    if (rgbaInt == unchecked((int)0xFF000000)) rgbaInt = unchecked((int)0xFFFFFFFF);

                    rgbaInt = ApplyFadeToLineColor(rgbaInt, fadeAlpha);
                    DrawThickBeam(origin, start, end, rgbaInt);
                }
            }


            private static int ApplyFadeToLineColor(int rgbaInt, float fade)
            {
                // VS RenderLine color packing here is the same we already use elsewhere: 0xAABBGGRR.
                // Some engines ignore alpha for line rendering; to ensure a visible fade we scale BOTH alpha and RGB.
                if (fade >= 0.999f) return rgbaInt;

                int a = (rgbaInt >> 24) & 0xFF;
                int b = (rgbaInt >> 16) & 0xFF;
                int g = (rgbaInt >> 8) & 0xFF;
                int r = (rgbaInt) & 0xFF;

                int na = (int)(a * fade);
                int nb = (int)(b * fade);
                int ng = (int)(g * fade);
                int nr = (int)(r * fade);

                if (na < 0) na = 0; else if (na > 255) na = 255;
                if (nb < 0) nb = 0; else if (nb > 255) nb = 255;
                if (ng < 0) ng = 0; else if (ng > 255) ng = 255;
                if (nr < 0) nr = 0; else if (nr > 255) nr = 255;

                return (na << 24) | (nb << 16) | (ng << 8) | nr;
            }

            private void DrawThickBeam(BlockPos origin, Vec3d start, Vec3d end, int rgbaInt)
            {
                RenderLine(origin, start, end, rgbaInt);

                for (int i = 0; i < BeamRingLines; i++)
                {
                    double ang = (Math.PI * 2.0 * i) / BeamRingLines;
                    double ox = Math.Cos(ang) * BeamRingRadius;
                    double oz = Math.Sin(ang) * BeamRingRadius;

                    Vec3d s2 = new Vec3d(start.X + ox, start.Y, start.Z + oz);
                    Vec3d e2 = new Vec3d(end.X + ox, end.Y, end.Z + oz);

                    RenderLine(origin, s2, e2, rgbaInt);
                }
            }

            private void RenderLine(BlockPos origin, Vec3d a, Vec3d b, int rgbaInt)
            {
                capi.Render.RenderLine(
                    origin,
                    (float)(a.X - origin.X), (float)(a.Y - origin.Y), (float)(a.Z - origin.Z),
                    (float)(b.X - origin.X), (float)(b.Y - origin.Y), (float)(b.Z - origin.Z),
                    rgbaInt
                );
            }

            public void Dispose() { }
        }

        // ---------------------------
        // Label renderer (outlined)
        // ---------------------------

        public class BeaconLabelRenderer : IRenderer
        {
            private readonly ICoreClientAPI capi;
            private readonly WaypointBeaconModSystem mod;

            private readonly Dictionary<string, LoadedTexture> cached = new Dictionary<string, LoadedTexture>();

            // When label style includes distance, we need to rebuild textures as the player moves.
            private Vec3d lastDistancePos;
            
            private static double Dist2PointToSegment(double px, double py, double ax, double ay, double bx, double by)
            {
                double abx = bx - ax;
                double aby = by - ay;
                double apx = px - ax;
                double apy = py - ay;

                double abLen2 = abx * abx + aby * aby;
                if (abLen2 <= 0.000001)
                {
                    // A and B are (almost) the same point
                    return apx * apx + apy * apy;
                }

                double t = (apx * abx + apy * aby) / abLen2;
                if (t < 0) t = 0;
                else if (t > 1) t = 1;

                double cx = ax + t * abx;
                double cy = ay + t * aby;

                double dx = px - cx;
                double dy = py - cy;
                return dx * dx + dy * dy;
            }


            public double RenderOrder => 1.25;
            public int RenderRange => 0;

            public BeaconLabelRenderer(ICoreClientAPI capi, WaypointBeaconModSystem mod)
            {
                this.capi = capi;
                this.mod = mod;
            }

            public void OnRenderFrame(float deltaTime, EnumRenderStage stage)
            {
                if (stage != EnumRenderStage.Ortho) return;

                

                // Distance labels must be re-generated as the player moves, otherwise you'll end up with
                // stale textures (and the cache will grow without bound). We keep it simple and rebuild
                // when the player has moved ~1 block.
                if (mod.LabelStyleMode == 1)
                {
                    var ent = capi.World?.Player?.Entity;
                    if (ent != null)
                    {
                        if (lastDistancePos == null)
                        {
                            lastDistancePos = new Vec3d(ent.Pos.X, ent.Pos.Y, ent.Pos.Z);
                        }
                        else
                        {
                            double dx = ent.Pos.X - lastDistancePos.X;
                            double dy = ent.Pos.Y - lastDistancePos.Y;
                            double dz = ent.Pos.Z - lastDistancePos.Z;

                            if ((dx * dx + dy * dy + dz * dz) > 1.0)
                            {
                                DisposeAllTextures();
                                lastDistancePos.Set(ent.Pos.X, ent.Pos.Y, ent.Pos.Z);
                            }
                        }
                    }
                }
                else
                {
                    lastDistancePos = null;
                }

var beacons = mod.GetVisibleBeacons();
                if (beacons == null || beacons.Count == 0) return;

                int fw = capi.Render.FrameWidth;
                int fh = capi.Render.FrameHeight;



                Vec3d camPos = capi.World.Player.Entity.CameraPos;
                double cx = fw / 2.0;
                double cy = fh / 2.0;
                int labelMode = mod.ShowLabelsMode;

                // AutoHide: when active, anchor label near the crosshair while centering horizontally on the beam.
                bool autoHideActive = false;
                double autoHideBeamX = 0;
                double autoHideBeamY = 0;

                foreach (var b in beacons)
                {
                    // Label visibility modes
                    if (labelMode == 1) continue; // Never

                    // In AutoHide mode, DO NOT gate visibility on the waypoint's own projected Y (it may be underground/offscreen).
                    // Instead, we test the beam near the player's current height window and anchor to the closest point on that beam.
                    double sx = 0;
                    double sy = 0;

                    autoHideActive = false;

                    if (labelMode == 2)
                    {
                        // AutoHide: show label only when crosshair is near the beacon beam.
                        // Tunables (you can tweak these):
                        double radiusPx = 30.0 * Vintagestory.API.Config.RuntimeEnv.GUIScale;
                        double r2 = radiusPx * radiusPx;

                        double beamX = Math.Floor(b.X) + 0.5;
                        double beamZ = Math.Floor(b.Z) + 0.5;

                        
                        // Allow auto-hide to work for beacons at any height; the proximity check
                        // against the beam in screen space is sufficient to gate visibility.

                        double dist = GameMath.Sqrt((Math.Floor(b.X) + 0.5 - camPos.X) * (Math.Floor(b.X) + 0.5 - camPos.X)
                                                  + (Math.Floor(b.Z) + 0.5 - camPos.Z) * (Math.Floor(b.Z) + 0.5 - camPos.Z));

                        // Pitch gate: require aiming at the beacon base or higher using triangle math.
                        // VS pitch convention is typically +down, so invert to get +up.
                        const double aimMarginDeg = 0.5;
                        double aimMarginRad = aimMarginDeg * (Math.PI / 180.0);
                        double pitchRad = capi.World.Player.Entity.Pos.Pitch;
                        if (Math.Abs(pitchRad) > Math.PI * 1.1)
                        {
                            pitchRad *= (Math.PI / 180.0);
                        }
                        double pitchUpRad = -pitchRad;
                        if (pitchUpRad > Math.PI) pitchUpRad -= Math.PI * 2.0;
                        if (pitchUpRad < -Math.PI) pitchUpRad += Math.PI * 2.0;
                        if (pitchUpRad > Math.PI / 2.0) pitchUpRad -= Math.PI;
                        if (pitchUpRad < -Math.PI / 2.0) pitchUpRad += Math.PI;
                        double beaconBaseY = Math.Floor(b.Y);
                        double baseDy = beaconBaseY - camPos.Y;
                        double pitchToBaseRad = Math.Atan2(baseDy, dist);

                        double effectivePitchUpRad = pitchUpRad * 3.0;
                        if (effectivePitchUpRad + aimMarginRad < pitchToBaseRad) continue;

                        int mapSizeY = capi.World.BlockAccessor.MapSizeY;
                        double yMin = camPos.Y - 64;
                        double yMax = camPos.Y + 64;
                        if (yMin < 0) yMin = 0;
                        if (yMax > mapSizeY - 1) yMax = mapSizeY - 1;

                        Vec3d beamA = new Vec3d(beamX, yMin, beamZ);
                        Vec3d beamB = new Vec3d(beamX, yMax, beamZ);

                        Vec3d scrA = MatrixToolsd.Project(beamA, capi.Render.PerspectiveProjectionMat, capi.Render.PerspectiveViewMat, fw, fh);
                        Vec3d scrB = MatrixToolsd.Project(beamB, capi.Render.PerspectiveProjectionMat, capi.Render.PerspectiveViewMat, fw, fh);

                        bool aOk = scrA.Z > 0;
                        bool bOk = scrB.Z > 0;
                        if (!aOk && !bOk) continue;

                        double ax = scrA.X;
                        double ay = fh - scrA.Y;
                        double bx = scrB.X;
                        double by = fh - scrB.Y;

                        double d2;
                        if (aOk && bOk)
                        {
                            d2 = Dist2PointToSegment(cx, cy, ax, ay, bx, by);
                        }
                        else
                        {
                            // If one endpoint is behind the camera, just use the visible endpoint as a point test.
                            double px = aOk ? ax : bx;
                            double py = aOk ? ay : by;
                            double dx = px - cx;
                            double dy = py - cy;
                            d2 = dx * dx + dy * dy;
                        }

                        if (d2 > r2) continue;

                        // Anchor point for AutoHide: closest point on the beam (in screen space).
                        if (aOk && bOk)
                        {
                            double abx = bx - ax;
                            double aby = by - ay;
                            double apx = cx - ax;
                            double apy = cy - ay;
                            double abLen2 = abx * abx + aby * aby;
                            double t = abLen2 <= 0.000001 ? 0.0 : (apx * abx + apy * aby) / abLen2;
                            if (t < 0) t = 0;
                            else if (t > 1) t = 1;
                            autoHideBeamX = ax + t * abx;
                            autoHideBeamY = ay + t * aby;
                        }
                        else
                        {
                            // Only one endpoint visible, use that as anchor.
                            autoHideBeamX = aOk ? ax : bx;
                            autoHideBeamY = aOk ? ay : by;
                        }

                        autoHideActive = true;
                        sx = autoHideBeamX;
                        sy = autoHideBeamY;
                    }
                    else
                    {
                        // Always: project the waypoint's label anchor as before.
                        Vec3d worldPos = new Vec3d(
                            Math.Floor(b.X) + 0.5,
                            Math.Floor(b.Y) + LabelWorldYOffsetBlocks,
                            Math.Floor(b.Z) + 0.5
                        );

                        Vec3d screen = MatrixToolsd.Project(
                            worldPos,
                            capi.Render.PerspectiveProjectionMat,
                            capi.Render.PerspectiveViewMat,
                            fw, fh
                        );

                        if (!(screen.Z > 0)) continue;

                        sx = screen.X;
                        sy = fh - screen.Y;

                        // If label goes off screen, don't render it at all
                        if (sx < 0 || sx > fw || sy < 0 || sy > fh) continue;
                    }

                    // From here down we render the label. In AutoHide mode, we already ensured proximity and anchor.
                    float fadeAlphaLbl = mod?.ComputeNearFadeAlpha(camPos, Math.Floor(b.X) + 0.5, Math.Floor(b.Y) + 0.5, Math.Floor(b.Z) + 0.5) ?? 1f;
                    if (fadeAlphaLbl <= 0.01f) continue;

                    Vec4f whiteA = new Vec4f(1f, 1f, 1f, fadeAlphaLbl);
                    Vec4f blackA = new Vec4f(0f, 0f, 0f, fadeAlphaLbl);
                    Vec4f iconColorA = new Vec4f(b.ColorRgba.X, b.ColorRgba.Y, b.ColorRgba.Z, b.ColorRgba.W * fadeAlphaLbl);

                    LoadedTexture outlineTex = mod.GetOrCreateLabelTexture(b, this, outline: true);
                    LoadedTexture fgTex = mod.GetOrCreateLabelTexture(b, this, outline: false);
                    if (fgTex == null) continue;

                    bool showIcons = mod.ShowIconsInLabels;
                    LoadedTexture iconTex = null;
                    float iconSize = 0f;
                    float iconGap = 0f;

                    if (showIcons && !string.IsNullOrEmpty(b.Icon) && mod.TryGetWaypointIconTexture(b.Icon, out iconTex))
                    {
                        // Keep icon at "single-line" height even when label is 2 lines
                        iconSize = GameMath.Min(fgTex.Height, mod.GetEffectiveLabelFontPx() * 1.3f);
                        iconGap = GameMath.Max(4f, iconSize * 0.25f);
                    }
                    else
                    {
                        iconTex = null;
                    }

                    float contentW = fgTex.Width + (iconTex != null ? (iconSize + iconGap) : 0f);
                    float contentH = GameMath.Max(fgTex.Height, iconTex != null ? iconSize : 0f);

                    float anchorX = (float)(autoHideActive ? autoHideBeamX : sx);
                    float baseX = anchorX - (contentW / 2f);

                    float baseY;
                    if (labelMode == 2 && autoHideActive)
                    {
                        // Keep label near the crosshair, with a small vertical drift based on aim along the beam.
                        double guiScale = Vintagestory.API.Config.RuntimeEnv.GUIScale;
                        double belowPx = 28.0 * guiScale;
                        double follow = 0.35; // 0 = fixed to crosshair, 1 = follow beam fully
                        double y = cy + belowPx + (autoHideBeamY - cy) * follow;

                        baseY = (float)y;
                    }
                    else
                    {
                        baseY = (float)(sy - contentH - LabelScreenYOffsetPx);
                    }

                    // Keep on-screen
                    baseY = GameMath.Clamp(baseY, 2f, fh - contentH - 2f);

                    float iconX = baseX;
                    float iconY = baseY + (contentH - iconSize) / 2f;

                    float textX = baseX + (iconTex != null ? (iconSize + iconGap) : 0f);
                    float textY = baseY + (contentH - fgTex.Height) / 2f;
if (iconTex != null)
                    {
                        // Outline pass for icon (black) using the same outline kernel as text
                        Vec4f black = blackA;

                        for (int ox = -OutlinePx; ox <= OutlinePx; ox++)
                        {
                            for (int oy = -OutlinePx; oy <= OutlinePx; oy++)
                            {
                                if (ox == 0 && oy == 0) continue;
                                if (Math.Abs(ox) + Math.Abs(oy) > OutlinePx + 1) continue;

                                capi.Render.Render2DTexture(iconTex.TextureId, iconX + ox, iconY + oy, iconSize, iconSize, 50f, black);
                            }
                        }

                        // Foreground (tinted)
                        capi.Render.Render2DTexture(iconTex.TextureId, iconX, iconY, iconSize, iconSize, 50f, iconColorA);
                    }

                    if (outlineTex != null)
                    {
                        for (int ox = -OutlinePx; ox <= OutlinePx; ox++)
                        {
                            for (int oy = -OutlinePx; oy <= OutlinePx; oy++)
                            {
                                if (ox == 0 && oy == 0) continue;
                                if (Math.Abs(ox) + Math.Abs(oy) > OutlinePx + 1) continue;

                                RenderLoadedTextureTint(outlineTex, textX + ox, textY + oy, blackA);
                            }
                        }
                    }

                    RenderLoadedTextureTint(fgTex, textX, textY, whiteA);
                }
            }

            
            private void RenderLoadedTextureTint(LoadedTexture tex, float x, float y, Vec4f color)
            {
                if (tex == null) return;
                capi.Render.Render2DTexture(tex.TextureId, x, y, tex.Width, tex.Height, 50f, color);
            }

private static double Clamp(double v, double lo, double hi)
            {
                if (v < lo) return lo;
                if (v > hi) return hi;
                return v;
            }

            public bool TryGetCached(string key, out LoadedTexture tex) => cached.TryGetValue(key, out tex);

            public void StoreCached(string key, LoadedTexture tex)
            {
                if (cached.TryGetValue(key, out var old) && old != null)
                {
                    old.Dispose();
                }
                cached[key] = tex;
            }

            public void DisposeAllTextures()
            {
                foreach (var kv in cached)
                {
                    kv.Value?.Dispose();
                }
                cached.Clear();
            }

            public void Dispose() => DisposeAllTextures();
        }
    }

    // ------------------------------------------------------------------------
    // Waypoint dialog Beacon toggle (1.21.6): companion dialog opened alongside
    // the vanilla "Modify waypoint" dialog. This avoids brittle composer injection.
    // ------------------------------------------------------------------------

    internal static class WaypointDialogBeaconPatch
    {
        // Cartographer-style injection:
        // - Transpile ComposeDialog to inject a "Beacon" switch during composition (so it's keyed and clickable)
        // - Postfix ComposeDialog to initialize the switch state
        // - Postfix onSave to persist (and for Add dialog, remember last choice)

        private const string EditDlg = "Vintagestory.GameContent.GuiDialogEditWayPoint";
        private const string AddDlg = "Vintagestory.GameContent.GuiDialogAddWayPoint";

        private const string BeaconSwitchKey = "wbBeaconSwitch";

        private static Harmony harmony;
        private static ICoreClientAPI capi;
        private static WaypointBeaconModSystem mod;

        public static void TryPatch(ICoreClientAPI api, WaypointBeaconModSystem modSystem)
        {
            capi = api;
            mod = modSystem;

            try
            {
                harmony = new Harmony("waypointbeacon.waypointdialog.beaconswitch");

                var patcherType = typeof(WaypointDialogBeaconPatch);

                // ---- EDIT dialog ----
                var editCompose = typeof(GuiDialogEditWayPoint).GetMethod("ComposeDialog", BindingFlags.Instance | BindingFlags.NonPublic);
                if (editCompose != null)
                {
                    harmony.Patch(editCompose,
                        transpiler: new HarmonyMethod(patcherType.GetMethod(nameof(ComposeDialog_Transpiler), BindingFlags.Static | BindingFlags.Public)),
                        postfix: new HarmonyMethod(patcherType.GetMethod(nameof(Post_GuiDialogEditWayPoint_ComposeDialog), BindingFlags.Static | BindingFlags.Public))
                    );
                    api.Logger.Warning("[WaypointBeacon] Patched {0}.ComposeDialog (inject + init).", EditDlg);
                }
                else
                {
                    api.Logger.Warning("[WaypointBeacon] Could not find {0}.ComposeDialog to patch.", EditDlg);
                }

                var editOnSave = typeof(GuiDialogEditWayPoint).GetMethod("onSave", BindingFlags.Instance | BindingFlags.NonPublic);
                if (editOnSave != null)
                {
                    harmony.Patch(editOnSave, postfix: new HarmonyMethod(patcherType.GetMethod(nameof(Post_GuiDialogEditWayPoint_onSave), BindingFlags.Static | BindingFlags.Public)));
                    api.Logger.Warning("[WaypointBeacon] Patched {0}.onSave (persist).", EditDlg);
                }
                else
                {
                    api.Logger.Warning("[WaypointBeacon] Could not find {0}.onSave to patch.", EditDlg);
                }

                // ---- ADD dialog ----
                var addCompose = typeof(GuiDialogAddWayPoint).GetMethod("ComposeDialog", BindingFlags.Instance | BindingFlags.NonPublic);
                if (addCompose != null)
                {
                    harmony.Patch(addCompose,
                        transpiler: new HarmonyMethod(patcherType.GetMethod(nameof(ComposeDialog_Transpiler), BindingFlags.Static | BindingFlags.Public)),
                        postfix: new HarmonyMethod(patcherType.GetMethod(nameof(Post_GuiDialogAddWayPoint_ComposeDialog), BindingFlags.Static | BindingFlags.Public))
                    );
                    api.Logger.Warning("[WaypointBeacon] Patched {0}.ComposeDialog (inject + init).", AddDlg);
                }
                else
                {
                    api.Logger.Warning("[WaypointBeacon] Could not find {0}.ComposeDialog to patch.", AddDlg);
                }

                var addOnSave = typeof(GuiDialogAddWayPoint).GetMethod("onSave", BindingFlags.Instance | BindingFlags.NonPublic);
                if (addOnSave != null)
                {
                    harmony.Patch(addOnSave, prefix: new HarmonyMethod(patcherType.GetMethod(nameof(Pre_GuiDialogAddWayPoint_onSave), BindingFlags.Static | BindingFlags.Public)), postfix: new HarmonyMethod(patcherType.GetMethod(nameof(Post_GuiDialogAddWayPoint_onSave), BindingFlags.Static | BindingFlags.Public)));
                    api.Logger.Warning("[WaypointBeacon] Patched {0}.onSave (persist + remember default).", AddDlg);
                }
                else
                {
                    api.Logger.Warning("[WaypointBeacon] Could not find {0}.onSave to patch.", AddDlg);
                }

                api.Logger.Warning("[WaypointBeacon] Beacon UI hook ACTIVE (Edit/Add waypoint dialogs via transpiler).");
            }
            catch (Exception e)
            {
                api.Logger.Warning("[WaypointBeacon] Failed to patch beacon UI: {0}", e);
            }
        }

        public static void Dispose()
        {
            try { harmony?.UnpatchAll("waypointbeacon.waypointdialog.beaconswitch"); } catch { }
        }

        private static void OnBeaconToggled(bool on)
        {
            // no-op; we persist on save (and remember default on Add save)
        }

        public static GuiComposer AddBeaconComponent(GuiComposer composer, ref ElementBounds leftColumn, ref ElementBounds rightColumn)
        {
            // Called during dialog ComposeDialog (before composer.Compose runs).
            // Match Cartographer's layout pattern: label left, switch right.
            return composer
                .AddStaticText(Vintagestory.API.Config.Lang.Get("Beacon"), CairoFont.WhiteSmallText(), leftColumn = leftColumn.BelowCopy(0, 9))
                .AddSwitch(OnBeaconToggled, rightColumn = rightColumn.BelowCopy(0, 5).WithFixedWidth(200), BeaconSwitchKey);
        }

        public static IEnumerable<CodeInstruction> ComposeDialog_Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            bool found = false;

            foreach (var instruction in instructions)
            {
                // Anchor at the existing control key used by vanilla: "waypoint-color"
                if (instruction.opcode == System.Reflection.Emit.OpCodes.Ldstr && (string)instruction.operand == "waypoint-color")
                {
                    // ElementBounds locals (leftColumn/rightColumn) are locals 0 and 1 in the vanilla dialogs
                    yield return new CodeInstruction(System.Reflection.Emit.OpCodes.Ldloca_S, 0);
                    yield return new CodeInstruction(System.Reflection.Emit.OpCodes.Ldloca_S, 1);
                    yield return new CodeInstruction(System.Reflection.Emit.OpCodes.Call,
                        typeof(WaypointDialogBeaconPatch).GetMethod(nameof(AddBeaconComponent), BindingFlags.Static | BindingFlags.Public));

                    found = true;
                }

                yield return instruction;
            }

            if (!found && capi != null)
            {
                capi.Logger.Warning("[WaypointBeacon] Transpiler: could not find anchor Ldstr \"waypoint-color\" in ComposeDialog; beacon switch not injected.");
            }
        }

        // --------------------
        // EDIT: init + persist
        // --------------------

        public static void Post_GuiDialogEditWayPoint_ComposeDialog(GuiDialogEditWayPoint __instance)
        {
            try
            {
                if (__instance?.SingleComposer == null || mod == null) return;

                object wpObj = TryGetWaypointObject(__instance);
                if (wpObj == null) return;

                bool on = mod.GetBeaconOnForWaypointObject(wpObj);
                TrySetSwitchState(__instance.SingleComposer, BeaconSwitchKey, on);
            }
            catch (Exception e)
            {
                capi?.Logger?.Warning("[WaypointBeacon] Edit ComposeDialog init failed: {0}", e);
            }
        }

        public static void Post_GuiDialogEditWayPoint_onSave(GuiDialogEditWayPoint __instance)
        {
            try
            {
                if (__instance?.SingleComposer == null || mod == null) return;

                object wpObj = TryGetWaypointObject(__instance);
                if (wpObj == null) return;

                bool on = TryGetSwitchState(__instance.SingleComposer, BeaconSwitchKey);
                mod.SetBeaconOnForWaypointObject(wpObj, on);
            }
            catch (Exception e)
            {
                capi?.Logger?.Warning("[WaypointBeacon] Edit onSave persist failed: {0}", e);
            }
        }

        // -------------------
        // ADD: init + persist
        // -------------------

        public static void Post_GuiDialogAddWayPoint_ComposeDialog(GuiDialogAddWayPoint __instance)
        {
            try
            {
                if (__instance?.SingleComposer == null || mod == null) return;

                // Add dialog default choice (remember last selection, or fall back to manager default)
                bool on = mod.AddDialogBeaconChoice;
                TrySetSwitchState(__instance.SingleComposer, BeaconSwitchKey, on);
            }
            catch (Exception e)
            {
                capi?.Logger?.Warning("[WaypointBeacon] Add ComposeDialog init failed: {0}", e);
            }
        }


        // --- Add Waypoint: capture existing waypoints before save so we can find the newly created one ---
        private static HashSet<string> addBeforeKeys;

        public static void Pre_GuiDialogAddWayPoint_onSave(GuiDialogAddWayPoint __instance)
        {
            try
            {
                addBeforeKeys = CaptureWaypointKeys();
            }
            catch
            {
                addBeforeKeys = null;
            }
        }

        public static void Post_GuiDialogAddWayPoint_onSave(GuiDialogAddWayPoint __instance)
        {
            try
            {
                if (__instance?.SingleComposer == null || mod == null) return;

                bool on = TryGetSwitchState(__instance.SingleComposer, BeaconSwitchKey);

                // Remember last choice for next time
                mod.SetLastAddBeaconChoice(on);

                // Apply to the newly created waypoint (it may appear in the list a tick later)
                if (TryApplyBeaconToNewlyCreatedWaypoint(on))
                {
                    addBeforeKeys = null;
                    return;
                }

                if (capi != null)
                {
                    capi.Event.RegisterCallback(_ => RetryApplyNewWaypoint(on, 0), 10);
                }
            }
            catch (Exception e)
            {
                capi?.Logger?.Warning("[WaypointBeacon] Add onSave persist failed: {0}", e);
            }
        }

        private static void RetryApplyNewWaypoint(bool on, int attempt)
        {
            try
            {
                if (attempt > 30) return; // ~300ms total

                if (TryApplyBeaconToNewlyCreatedWaypoint(on))
                {
                    addBeforeKeys = null;
                    return;
                }

                capi?.Event?.RegisterCallback(_ => RetryApplyNewWaypoint(on, attempt + 1), 10);
            }
            catch { }
        }

        private static bool TryApplyBeaconToNewlyCreatedWaypoint(bool on)
        {
            if (capi == null || mod == null) return false;

            if (!TryGetWaypointList(out var list) || list == null) return false;

            // Find a waypoint key that did not exist before save.
            object newest = null;

            for (int i = 0; i < list.Count; i++)
            {
                object wp = list[i];
                if (wp == null) continue;

                if (!TryGetWaypointData(wp, out double x, out double y, out double z, out string title)) continue;
                string key = MakePinKeyLocal(x, y, z, title);

                if (addBeforeKeys != null && addBeforeKeys.Contains(key)) continue;

                newest = wp;
            }

            if (newest == null) return false;

            mod.SetBeaconOnForWaypointObject(newest, on);
            mod.MarkWaypointSeen(newest);
            return true;
        }

        private static bool TryGetWaypointList(out System.Collections.IList list)
        {
            list = null;
            try
            {
                var mapManager = capi.ModLoader.GetModSystem<WorldMapManager>();
                if (mapManager?.MapLayers == null) return false;

                object layer = mapManager.MapLayers.FirstOrDefault(l =>
                    l != null && l.GetType().Name.IndexOf("WaypointMapLayer", StringComparison.OrdinalIgnoreCase) >= 0);

                if (layer == null) return false;

                object listObj =
                    TryGetMember(layer, "ownWaypoints") ??
                    TryGetMember(layer, "OwnWaypoints") ??
                    TryGetMember(layer, "waypoints") ??
                    TryGetMember(layer, "Waypoints");

                list = listObj as System.Collections.IList;
                return list != null;
            }
            catch
            {
                return false;
            }
        }


        private static object TryGetMember(object obj, string name)
        {
            if (obj == null || string.IsNullOrEmpty(name)) return null;
            try
            {
                var t = obj.GetType();
                const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

                var p = t.GetProperty(name, flags);
                if (p != null) return p.GetValue(obj);

                var f = t.GetField(name, flags);
                if (f != null) return f.GetValue(obj);
            }
            catch
            {
                // ignore
            }

            return null;
        }
        private static HashSet<string> CaptureWaypointKeys()
        {
            var set = new HashSet<string>();

            if (!TryGetWaypointList(out var list) || list == null) return set;

            for (int i = 0; i < list.Count; i++)
            {
                object wp = list[i];
                if (wp == null) continue;

                if (!TryGetWaypointData(wp, out double x, out double y, out double z, out string title)) continue;
                set.Add(MakePinKeyLocal(x, y, z, title));
            }

            return set;
        }

        private static bool TryGetWaypointData(object wp, out double x, out double y, out double z, out string title)
        {
            x = y = z = 0;
            title = "";

            if (wp == null) return false;

            title =
                TryGetStringFrom(wp, "Title", "title", "Name", "name", "Text", "text") ?? "";

            // Try a position member first
            object pos =
                TryGetMember(wp, "Position") ??
                TryGetMember(wp, "position") ??
                TryGetMember(wp, "Pos") ??
                TryGetMember(wp, "pos") ??
                TryGetMember(wp, "WorldPos") ??
                TryGetMember(wp, "worldPos");

            if (pos != null && TryGetXYZ(pos, out x, out y, out z)) return true;

            // Fallback: direct X/Y/Z on waypoint
            return TryGetXYZ(wp, out x, out y, out z);
        }

        private static string TryGetStringFrom(object obj, params string[] names)
        {
            foreach (var n in names)
            {
                object v = TryGetMember(obj, n);
                if (v is string s) return s;
            }
            return null;
        }

        private static bool TryGetXYZ(object obj, out double x, out double y, out double z)
        {
            x = y = z = 0;
            if (obj == null) return false;

            object ox = TryGetMember(obj, "X") ?? TryGetMember(obj, "x");
            object oy = TryGetMember(obj, "Y") ?? TryGetMember(obj, "y");
            object oz = TryGetMember(obj, "Z") ?? TryGetMember(obj, "z");

            if (ox == null || oy == null || oz == null) return false;

            try
            {
                x = Convert.ToDouble(ox);
                y = Convert.ToDouble(oy);
                z = Convert.ToDouble(oz);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static string MakePinKeyLocal(double x, double y, double z, string title)
        {
            int xi = (int)Math.Round(x);
            int yi = (int)Math.Round(y);
            int zi = (int)Math.Round(z);
            title = title ?? "";
            return $"{xi},{yi},{zi}|{title}";
        }

        // ---- Switch helpers (reflection-safe across VS versions) ----
        private static bool TryGetSwitchState(GuiComposer composer, string key)
        {
            try
            {
                object sw = composer.GetSwitch(key);
                if (sw == null) return false;

                var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                var pOn = sw.GetType().GetProperty("On", flags);
                if (pOn != null && pOn.CanRead) return (bool)pOn.GetValue(sw);

                var fOn = sw.GetType().GetField("On", flags) ?? sw.GetType().GetField("on", flags);
                if (fOn != null) return (bool)fOn.GetValue(sw);
            }
            catch { }
            return false;
        }

        private static void TrySetSwitchState(GuiComposer composer, string key, bool on)
        {
            try
            {
                object sw = composer.GetSwitch(key);
                if (sw == null) return;

                var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                var pOn = sw.GetType().GetProperty("On", flags);
                if (pOn != null && pOn.CanWrite)
                {
                    pOn.SetValue(sw, on);
                    return;
                }

                var mSet = sw.GetType().GetMethod("SetValue", flags, null, new[] { typeof(bool) }, null);
                if (mSet != null)
                {
                    mSet.Invoke(sw, new object[] { on });
                    return;
                }

                var fOn = sw.GetType().GetField("On", flags) ?? sw.GetType().GetField("on", flags);
                if (fOn != null) fOn.SetValue(sw, on);
            }
            catch { }
        }

        // ---- Waypoint object lookup ----
        private static object TryGetWaypointObject(object dlg)
        {
            if (dlg == null) return null;

            var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

            // Common names across versions
            string[] names =
            {
            "waypoint", "WayPoint", "wayPoint",
            "selectedWaypoint", "SelectedWaypoint",
            "editWaypoint", "EditWaypoint",
            "curWaypoint", "CurWaypoint",
            "creatingWaypoint", "CreatingWaypoint"
        };

            Type t = dlg.GetType();

            foreach (string n in names)
            {
                var p = t.GetProperty(n, flags);
                if (p != null)
                {
                    object val = p.GetValue(dlg);
                    if (val != null) return val;
                }

                var f = t.GetField(n, flags);
                if (f != null)
                {
                    object val = f.GetValue(dlg);
                    if (val != null) return val;
                }
            }

            // Fallback: scan for any field/property whose type name contains "waypoint"
            foreach (var f in t.GetFields(flags))
            {
                try
                {
                    object val = f.GetValue(dlg);
                    if (val == null) continue;

                    string tn = val.GetType().FullName ?? val.GetType().Name ?? "";
                    if (tn.IndexOf("waypoint", StringComparison.OrdinalIgnoreCase) >= 0) return val;
                }
                catch { }
            }

            foreach (var p in t.GetProperties(flags))
            {
                try
                {
                    if (!p.CanRead) continue;
                    object val = p.GetValue(dlg);
                    if (val == null) continue;

                    string tn = val.GetType().FullName ?? val.GetType().Name ?? "";
                    if (tn.IndexOf("waypoint", StringComparison.OrdinalIgnoreCase) >= 0) return val;
                }
                catch { }
            }

            return null;
        }
    }
}
