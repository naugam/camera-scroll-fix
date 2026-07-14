using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Security.Permissions;
using BepInEx;
using BepInEx.Logging;
using UnityEngine;
using MonoMod.RuntimeDetour;
using SBCameraScroll;
using RWCustom;

#pragma warning disable CS0618
[assembly: SecurityPermission(SecurityAction.RequestMinimum, SkipVerification = true)]
#pragma warning restore CS0618

namespace CameraScrollFix;

[BepInPlugin(PLUGIN_GUID, PLUGIN_NAME, PLUGIN_VERSION)]
[BepInDependency("SBCameraScroll", BepInDependency.DependencyFlags.HardDependency)]
public class FlatMod : BaseUnityPlugin
{
    public const string PLUGIN_GUID = "naugam.camera_scroll_fix";
    public const string PLUGIN_NAME = "Camera Scroll Fix";
    public const string PLUGIN_VERSION = "1.0.3";

    private const bool FLIP_Y = false;

    internal static ManualLogSource Log;

    private bool init;

    private static readonly Dictionary<int, Texture2D> flat_cpu = new();

    private static readonly Dictionary<int, string> active_room = new();

    private static readonly HashSet<string> blacklist =
        new(StringComparer.OrdinalIgnoreCase) { "GW_TOWER01", "SL_ROOF04", "UG_B06", "UW_PREGATE", "LF_D09", "DS_C04", "LC_dome", "LC_FINAL", "GW_ARTYNIGHTMARE", "GW_ARTYSCENES", "MS_HEART", "MS_bitteraerie1" };

    private static readonly HashSet<string> logged_names =
        new(StringComparer.OrdinalIgnoreCase);

    public void OnEnable()
    {
        Log = Logger;
        On.RainWorld.OnModsInit += RainWorld_OnModsInit;
    }

    private void RainWorld_OnModsInit(On.RainWorld.orig_OnModsInit orig, RainWorld self)
    {
        orig(self);
        if (init) return;
        init = true;
        try
        {
            MethodInfo target = typeof(RoomCameraMod).GetMethod(
                nameof(RoomCameraMod.RoomCameraMod_LoadOneScreenOrFullRoomTexture),
                BindingFlags.Public | BindingFlags.Static);
            _ = new Hook(target,
                typeof(FlatMod).GetMethod(nameof(LoadTexHook), BindingFlags.Public | BindingFlags.Static));

            On.RoomCamera.PixelColorAtCoordinate += RoomCamera_PixelColorAtCoordinate;
            On.RoomCamera.LitAtCoordinate        += RoomCamera_LitAtCoordinate;
            On.RoomCamera.DepthAtCoordinate      += RoomCamera_DepthAtCoordinate;

            Log.LogInfo($"{PLUGIN_NAME}: hooks applied.");
        }
        catch (Exception e) { Log.LogError($"{PLUGIN_NAME}: hook failed. {e}"); }
    }

    public static void LoadTexHook(Action<RoomCamera> orig, RoomCamera rc)
    {
        int cam = rc.cameraNumber;
        if (cam < 0 || cam > 3) cam = 0;

        active_room[cam] = "";

        try { if (TryLoadFlat(rc, cam)) return; }
        catch (Exception e) { Log.LogError($"{PLUGIN_NAME}: {e}"); }

        orig(rc);
    }

    private static bool TryLoadFlat(RoomCamera rc, int cam)
    {
        Room room = rc.loadingRoom ?? rc.room;
        if (room?.abstractRoom == null) return false;

        string an = room.abstractRoom.name ?? "";
        string fn = room.abstractRoom.FileName ?? "";

        if (logged_names.Add(an))
            Log.LogInfo($"{PLUGIN_NAME}: room name='{an}' file='{fn}'");

        if (blacklist.Contains(an) || blacklist.Contains(fn))
        {
            Log.LogInfo($"{PLUGIN_NAME}: '{an}' blacklisted -> stitched screens.");
            return false;
        }

        string room_name = fn;
        string flat_path = WorldLoader.FindRoomFile(room_name, false, "_flat.png");
        if (!File.Exists(flat_path)) return false;

        if (AbstractRoomMod.CalculateLevelTextureRectangle(room_name) is not RectInt rect)
            return false;

        if (!flat_cpu.TryGetValue(cam, out Texture2D flat) || flat == null)
        {
            flat = new Texture2D(4, 4, TextureFormat.ARGB32, mipChain: false)
            {
                anisoLevel = 0,
                filterMode = FilterMode.Point,
                wrapMode   = TextureWrapMode.Clamp
            };
            flat_cpu[cam] = flat;
        }

        flat.LoadImage(AssetManager.PreLoadTexture(flat_path), markNonReadable: false);

        if (flat.width != rect.width || flat.height != rect.height)
        {
            Log.LogInfo($"{PLUGIN_NAME}: flat size mismatch for {room_name} " +
                        $"(flat {flat.width}x{flat.height}, expected {rect.width}x{rect.height}); " +
                        "falling back to stitched screens.");
            return false;
        }

        float shortcutScore = CheckFlatTextureShortcutAlignments(rc, flat);
        if (shortcutScore != 1) //unexpected score
        {
            Log.LogInfo($"{PLUGIN_NAME}: shortcut score for {room_name} is {shortcutScore}.");
            if (shortcutScore < 0.5f)
            {
                Log.LogInfo($"{PLUGIN_NAME}: falling back to stitched screens for {room_name}.");
                return false;
            }
        }

        RenderTexture rt = rc.Render_Texture();
        if (!Util.Util_UpdateRenderTexture(rt, rect)) return false;

        Graphics.CopyTexture(flat, rt);
        active_room[cam] = fn;
        return true;
    }

    private static float CheckFlatTextureShortcutAlignments(RoomCamera rc, Texture2D flat)
    {
        Room room = rc.loadingRoom ?? rc.room;
        Vector2 minCameraPosition = room.abstractRoom.GetFields().min_camera_position;
        IntVector2 cameraOffset = new(Mathf.RoundToInt(minCameraPosition.x), Mathf.RoundToInt(minCameraPosition.y));

        int successes = 0;
        int tests = 0;
        foreach (ShortcutData sc in room.shortcuts)
        {
            try
            {
                foreach (IntVector2 pos in sc.path)
                {
                    tests++;
                    //check the middle of this shortcut for the shortcut cutout color
                    IntVector2 samplePos = pos * 20 + new IntVector2(10, 10) - cameraOffset; //middle of tile
                    Color col = flat.GetPixel(samplePos.x, samplePos.y);
                    //the 3rd green bit should be 1, and the blue value should be 0
                    if ((Mathf.RoundToInt(col.g * 255) & 8) == 8 && col.b == 0)
                        successes++;
                }
            }
            catch (Exception ex) { Log.LogError(ex); }
        }

        return (float)successes / (float)tests;
    }

    private static bool TryGetFlat(RoomCamera rc, out Texture2D flat, out Vector2 min)
    {
        flat = null; min = default;
        int cam = rc.cameraNumber;
        if (cam < 0 || cam > 3) cam = 0;
        if (rc.room is not Room room) return false;
        if (!active_room.TryGetValue(cam, out string rn) || rn == "") return false;
        if (rn != room.abstractRoom.FileName) return false;
        if (!flat_cpu.TryGetValue(cam, out flat) || flat == null) return false;
        min = room.abstractRoom.GetFields().min_camera_position;
        return true;
    }

    private static Color ReadFlat(Texture2D flat, Vector2 local)
    {
        int x = Mathf.FloorToInt(local.x);
        int y = Mathf.FloorToInt(local.y);
        if (FLIP_Y) y = flat.height - 1 - y;
        return flat.GetPixel(x, y);
    }

    private static Color RoomCamera_PixelColorAtCoordinate(
        On.RoomCamera.orig_PixelColorAtCoordinate orig, RoomCamera rc, Vector2 position)
    {
        if (!TryGetFlat(rc, out Texture2D flat, out Vector2 min))
            return orig(rc, position);

        Color pixel_color = ReadFlat(flat, position - min);

        if (pixel_color.r == 1f && pixel_color.g == 1f && pixel_color.b == 1f)
            return rc.paletteTexture.GetPixel(0, 7);

        int red = Mathf.FloorToInt(pixel_color.r * 255f);
        float t = 0f;
        if (red > 90) red -= 90; else t = 1f;

        int div = Mathf.FloorToInt((float)red / 30f);
        int rem = (red - 1) % 30;
        return Color.Lerp(
            Color.Lerp(rc.paletteTexture.GetPixel(rem, div + 3), rc.paletteTexture.GetPixel(rem, div), t),
            rc.paletteTexture.GetPixel(1, 7),
            (float)rem * (1f - rc.paletteTexture.GetPixel(9, 7).r) / 30f);
    }

    private static bool? RoomCamera_LitAtCoordinate(
        On.RoomCamera.orig_LitAtCoordinate orig, RoomCamera rc, Vector2 position)
    {
        if (!TryGetFlat(rc, out Texture2D flat, out Vector2 min))
            return orig(rc, position);

        Color pixel_color = ReadFlat(flat, position - min);
        if (pixel_color.r == 1f && pixel_color.g == 1f && pixel_color.b == 1f)
            return null;
        return Mathf.FloorToInt(pixel_color.r * 255f) > 90;
    }

    private static float RoomCamera_DepthAtCoordinate(
        On.RoomCamera.orig_DepthAtCoordinate orig, RoomCamera rc, Vector2 position)
    {
        if (!TryGetFlat(rc, out Texture2D flat, out Vector2 min))
            return orig(rc, position);

        Color pixel_color = ReadFlat(flat, position - min);
        if (pixel_color.r == 1f && pixel_color.g == 1f && pixel_color.b == 1f)
            return 1f;

        int red = Mathf.FloorToInt(pixel_color.r * 255f);
        if (red > 90) red -= 90;
        return (float)((red - 1) % 30) / 30f;
    }
}
