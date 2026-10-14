using GTA;
using GTA.Math;
using GTA.UI;

public sealed class LegacyScript : Script
{
    public LegacyScript()
    {
        Tick += (s, e) => UseRemovedApi();
    }

    private static void UseRemovedApi()
    {
        FeedPost post = Notification.PostTicker("hello", true, false);
        post.Delete();
        bool same = post == Notification.PostTickerForced("a", false, true);
        same |= post != Notification.PostTickerWithTokens("b", false, true);
        int handle = post.Handle + post.GetHashCode();
        Notification.PostUnlock("m", "t", (FeedUnlockIcon)0);
        Notification.PostUnlockTitleUpdate("m", "t", (FeedUnlockIcon)0);

        Camera cam = World.CreateCamera(Vector3.Zero, Vector3.Zero, 50f);
        cam.InterpTo(cam, 1000, CamFrameInterpolatorCurveType.Linear, CamFrameInterpolatorCurveType.Linear, EulerRotationOrder.YXZ);

        CamViewMode mode = GameplayCamera.SetCamViewModeForContext(CamViewModeContext.OnFoot, CamViewMode.FirstPerson);

        Ped ped = Game.Player.Character;
        ped.Task.PlaneChase(ped.CurrentVehicle, Vector3.Zero, Vector3.WorldUp);
        ped.CurrentVehicle.SetSpecialFlightModeAllowed(true);
        bool selected = ped.Weapons.IsWeaponValid("WEAPON_PISTOL", true);

        if (!same || handle == 0 || mode == CamViewMode.ThirdPersonFar || selected)
        {
            Notification.Show("ok");
        }
    }
}
