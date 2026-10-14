using GTA;
using GTA.Math;
using GTA.NaturalMotion;

namespace ShvdnCompat.Shims.V2
{
    // Members of the C++/CLI v2 API (2.10.3 - 2.10.10) that changed signature when the v2 API was rewritten
    // in C#. The user's legacy install already used 2.11.6, which is identical to the nightly v2 API, so these
    // only matter for very old v2 scripts.

    [ShimType("GTA.Player")]
    internal static class PlayerShims
    {
        [Shim(Instance = true)]
        public static int get_RemainingSprintTime(Player self) => (int)self.RemainingSprintTime;

        [Shim(Instance = true)]
        public static int get_RemainingUnderwaterTime(Player self) => (int)self.RemainingUnderwaterTime;
    }

    [ShimType("GTA.Math.Quaternion")]
    internal static class QuaternionShims
    {
        [Shim]
        public static Quaternion op_Division(Quaternion left, float right)
        {
            return new Quaternion(left.X / right, left.Y / right, left.Z / right, left.W / right);
        }
    }

    [ShimType("GTA.NaturalMotion.Message")]
    internal static class MessageShims
    {
        [Shim(Instance = true)]
        public static void SetArgument(Message self, string message, double value) => self.SetArgument(message, (float)value);
    }

    // NaturalMotion helper setters that took int before they were typed as enums.

    [ShimType("GTA.NaturalMotion.ActivePoseHelper")]
    internal static class ActivePoseHelperShims
    {
        [Shim(Instance = true)]
        public static void set_AnimSource(ActivePoseHelper self, int value) => self.AnimSource = (AnimSource)value;
    }

    [ShimType("GTA.NaturalMotion.AnimPoseHelper")]
    internal static class AnimPoseHelperShims
    {
        [Shim(Instance = true)]
        public static void set_AnimSource(AnimPoseHelper self, int value) => self.AnimSource = (AnimSource)value;
    }

    [ShimType("GTA.NaturalMotion.ArmsWindmillHelper")]
    internal static class ArmsWindmillHelperShims
    {
        [Shim(Instance = true)]
        public static void set_AdaptiveMode(ArmsWindmillHelper self, int value) => self.AdaptiveMode = (AdaptiveMode)value;

        [Shim(Instance = true)]
        public static void set_MirrorMode(ArmsWindmillHelper self, int value) => self.MirrorMode = (MirrorMode)value;
    }

    [ShimType("GTA.NaturalMotion.ConfigureBalanceHelper")]
    internal static class ConfigureBalanceHelperShims
    {
        [Shim(Instance = true)]
        public static void set_FallType(ConfigureBalanceHelper self, int value) => self.FallType = (FallType)value;
    }

    [ShimType("GTA.NaturalMotion.ConfigureBulletsHelper")]
    internal static class ConfigureBulletsHelperShims
    {
        [Shim(Instance = true)]
        public static void set_RbTwistAxis(ConfigureBulletsHelper self, int value) => self.RbTwistAxis = (RbTwistAxis)value;

        [Shim(Instance = true)]
        public static void set_TorqueFilterMode(ConfigureBulletsHelper self, int value) => self.TorqueFilterMode = (TorqueFilterMode)value;

        [Shim(Instance = true)]
        public static void set_TorqueMode(ConfigureBulletsHelper self, int value) => self.TorqueMode = (TorqueMode)value;

        [Shim(Instance = true)]
        public static void set_TorqueSpinMode(ConfigureBulletsHelper self, int value) => self.TorqueSpinMode = (TorqueSpinMode)value;
    }

    [ShimType("GTA.NaturalMotion.ConfigureBulletsExtraHelper")]
    internal static class ConfigureBulletsExtraHelperShims
    {
        [Shim(Instance = true)]
        public static void set_RbTwistAxis(ConfigureBulletsExtraHelper self, int value) => self.RbTwistAxis = (RbTwistAxis)value;

        [Shim(Instance = true)]
        public static void set_TorqueFilterMode(ConfigureBulletsExtraHelper self, int value) => self.TorqueFilterMode = (TorqueFilterMode)value;

        [Shim(Instance = true)]
        public static void set_TorqueMode(ConfigureBulletsExtraHelper self, int value) => self.TorqueMode = (TorqueMode)value;

        [Shim(Instance = true)]
        public static void set_TorqueSpinMode(ConfigureBulletsExtraHelper self, int value) => self.TorqueSpinMode = (TorqueSpinMode)value;
    }

    [ShimType("GTA.NaturalMotion.ConfigureConstraintsHelper")]
    internal static class ConfigureConstraintsHelperShims
    {
        // British spelling in the C++/CLI API.
        [Shim(Instance = true)]
        public static void set_BespokeBehaviour(ConfigureConstraintsHelper self, bool value) => self.BespokeBehavior = value;
    }

    [ShimType("GTA.NaturalMotion.FireWeaponHelper")]
    internal static class FireWeaponHelperShims
    {
        [Shim(Instance = true)]
        public static void set_GunHandEnum(FireWeaponHelper self, int value) => self.GunHandEnum = (Hand)value;
    }

    [ShimType("GTA.NaturalMotion.GrabHelper")]
    internal static class GrabHelperShims
    {
        [Shim(Instance = true)]
        public static void set_TurnToTarget(GrabHelper self, int value) => self.TurnToTarget = (TurnType)value;
    }

    [ShimType("GTA.NaturalMotion.HighFallHelper")]
    internal static class HighFallHelperShims
    {
        [Shim(Instance = true)]
        public static void set_Arms2LegsSync(HighFallHelper self, int value) => self.Arms2LegsSync = (Synchroisation)value;
    }

    [ShimType("GTA.NaturalMotion.SmartFallHelper")]
    internal static class SmartFallHelperShims
    {
        [Shim(Instance = true)]
        public static void set_Arms2LegsSync(SmartFallHelper self, int value) => self.Arms2LegsSync = (Synchroisation)value;
    }

    [ShimType("GTA.NaturalMotion.RegisterWeaponHelper")]
    internal static class RegisterWeaponHelperShims
    {
        [Shim(Instance = true)]
        public static void set_Hand(RegisterWeaponHelper self, int value) => self.Hand = (Hand)value;
    }

    [ShimType("GTA.NaturalMotion.SetWeaponModeHelper")]
    internal static class SetWeaponModeHelperShims
    {
        [Shim(Instance = true)]
        public static void set_WeaponMode(SetWeaponModeHelper self, int value) => self.WeaponMode = (WeaponMode)value;
    }
}
