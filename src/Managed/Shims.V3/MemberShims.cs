using System;
using GTA;
using GTA.Math;
using GTA.Native;

namespace ShvdnCompat.Shims.V3
{
    // Members removed or re-signatured in the v3 API between the legacy builds (stable 3.0.0 - 3.6.0 and the
    // v3.7.0 nightlies up to nightly.26) and the current nightly. Each shim forwards to the current API and
    // keeps the old behaviour where the old behaviour was well defined.

    [ShimType("GTA.Camera")]
    internal static class CameraShims
    {
        // nightly.26 had an extra EulerRotationOrder parameter; SET_CAM_ACTIVE_WITH_INTERP only takes 5
        // arguments, so the extra value was ignored by the game anyway.
        [Shim(Instance = true)]
        public static void InterpTo(Camera self, Camera destinationCam, int duration, CamFrameInterpolatorCurveType graphTypePos, CamFrameInterpolatorCurveType graphTypeRot, EulerRotationOrder rotOrder)
        {
            self.InterpTo(destinationCam, duration, graphTypePos, graphTypeRot);
        }
    }

    [ShimType("GTA.GameplayCamera")]
    internal static class GameplayCameraShims
    {
        // nightly.26 returned a CamViewMode (read from a native that returns nothing); the current API returns
        // void. Return the mode that is in effect after the call instead of garbage.
        [Shim]
        public static CamViewMode SetCamViewModeForContext(CamViewModeContext context, CamViewMode viewMode)
        {
            GameplayCamera.SetCamViewModeForContext(context, viewMode);
            return GameplayCamera.GetCamViewModeForContext(context);
        }

        // Stable 3.0.0 - 3.6.0.
        [Shim]
        public static void ClampYaw(float min, float max)
        {
            Function.Call(Hash.SET_THIRD_PERSON_CAM_RELATIVE_HEADING_LIMITS_THIS_UPDATE, min, max);
        }

        // Stable 3.0.0 - 3.6.0.
        [Shim]
        public static void ClampPitch(float min, float max)
        {
            Function.Call(Hash.SET_THIRD_PERSON_CAM_RELATIVE_PITCH_LIMITS_THIS_UPDATE, min, max);
        }
    }

    [ShimType("GTA.TaskInvoker")]
    internal static class TaskInvokerShims
    {
        // nightly.26 passed (plane, runWayStart) to TASK_PLANE_CHASE as the target and offset; runWayEnd was
        // an extra argument the native ignores. PlaneChase(Entity, Vector3) issues the exact same native call.
        [Shim(Instance = true)]
        public static void PlaneChase(TaskInvoker self, Vehicle plane, Vector3 runWayStart, Vector3 runWayEnd)
        {
            self.PlaneChase(plane, runWayStart);
        }
    }

    [ShimType("GTA.Vehicle")]
    internal static class VehicleShims
    {
        // Became the setter of the Vehicle.SpecialFlightModeAllowed property.
        [Shim(Instance = true)]
        public static void SetSpecialFlightModeAllowed(Vehicle self, bool allowed)
        {
            self.SpecialFlightModeAllowed = allowed;
        }
    }

    [ShimType("GTA.WeaponCollection")]
    internal static class WeaponCollectionShims
    {
        // Misnamed in nightly.26; it always selected the weapon. Renamed to Select(string, bool).
        [Shim(Instance = true)]
        public static bool IsWeaponValid(WeaponCollection self, string weaponName, bool forceInHand)
        {
            return self.Select(weaponName, forceInHand);
        }
    }

    [ShimType("GTA.WeaponAsset")]
    internal static class WeaponAssetShims
    {
        // Stable 3.0.0 - 3.1.0: WeaponAsset.LocalizedName.
        [Shim(Instance = true)]
        public static string get_LocalizedName(ref WeaponAsset self)
        {
            return Game.GetLocalizedString(Weapon.GetDisplayNameFromHash((WeaponHash)self.Hash));
        }
    }

    [ShimType("GTA.World")]
    internal static class WorldShims
    {
        // Stable 3.0.0 - 3.4.0 had CreateRandomVehicle(Vector3, float) before the predicate parameter was added.
        [Shim]
        public static Vehicle CreateRandomVehicle(Vector3 position, float heading)
        {
            return World.CreateRandomVehicle(position, heading, (Func<Model, bool>)null);
        }
    }
}
