using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon.Common;

namespace SanAndreasUnity.VRChat
{
    /// <summary>
    /// The one place vehicle input is received, forwarded to whichever seat the player is sitting in.
    ///
    /// VRChat delivers input events to every UdonBehaviour in the scene that implements them - not only to
    /// the one the player is interacting with. With an input handler on each seat, and a seat in every
    /// parked car, one tap of the throttle was running over a thousand Udon programs, all but one of them
    /// to discover they were not the car being driven. Udon is interpreted and perhaps two orders of
    /// magnitude slower than compiled C#, so that is not a cost worth paying every frame.
    ///
    /// Holding the handlers here instead makes it one program per event regardless of how many vehicles
    /// exist. The seat registers itself when the player sits down and clears itself when they get out.
    /// </summary>
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class GtaVehicleInput : UdonSharpBehaviour
    {
        /// <summary> The seat the local player currently occupies, or null when they are on foot. </summary>
        [HideInInspector] public GtaPlayerVehicleSeat activeSeat;

        /// <summary> Called by a seat when the local player sits down in it. </summary>
        public void SetActiveSeat(GtaPlayerVehicleSeat seat)
        {
            activeSeat = seat;
        }

        /// <summary> Called by a seat when the local player leaves it. </summary>
        public void ClearActiveSeat(GtaPlayerVehicleSeat seat)
        {
            // Only the seat that claimed the player may release them. Without this check, a seat whose
            // exit event arrives late - after the player has already got into another car - would clear
            // the new seat and leave the player unable to steer.
            if (activeSeat == seat)
                activeSeat = null;
        }

        public override void InputMoveVertical(float value, UdonInputEventArgs args)
        {
            if (activeSeat != null)
                activeSeat.SetThrottle(value);
        }

        public override void InputMoveHorizontal(float value, UdonInputEventArgs args)
        {
            if (activeSeat != null)
                activeSeat.SetSteer(value);
        }

        // The station has disableStationExit set, so VRChat no longer offers its own "Get Up" - if the
        // world does not provide an exit the player is stuck in the car. Jump is the convention other
        // VRChat vehicles use, so it is the key players will try first.
        public override void InputJump(bool value, UdonInputEventArgs args)
        {
            if (value && activeSeat != null)
                activeSeat.LeaveVehicle();
        }

        // Handbrake moves to Use, since jump is now the exit. Held, not toggled, as in the game.
        public override void InputUse(bool value, UdonInputEventArgs args)
        {
            if (activeSeat != null)
                activeSeat.SetHandbrake(value);
        }
    }
}
