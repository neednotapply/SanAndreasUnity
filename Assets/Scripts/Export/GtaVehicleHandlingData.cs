using UnityEngine;

namespace SanAndreasUnity.Export
{
    /// <summary>
    /// Per-vehicle physics parameters lifted from GTA's handling.cfg.
    ///
    /// Each vehicle in the game has its own mass, gearing, grip and steering limits - that is what makes a
    /// Rhino feel nothing like an Infernus. Handling data is only available while the game files are
    /// loaded, so it is captured at export time and read back when the scene is assembled.
    ///
    /// Parallel arrays indexed together, keyed by model name.
    /// </summary>
    public class GtaVehicleHandlingData : ScriptableObject
    {
        public string[] modelNames;

        /// <summary> Mass in kg. </summary>
        public float[] mass;

        /// <summary> Top speed, from the transmission's max velocity. </summary>
        public float[] maxSpeed;

        /// <summary> Engine acceleration. </summary>
        public float[] engineAccel;

        /// <summary> Braking deceleration. </summary>
        public float[] brakeDecel;

        /// <summary> Maximum steering angle, degrees. </summary>
        public float[] steeringLock;

        /// <summary> Grip multiplier - higher resists sliding. </summary>
        public float[] traction;

        /// <summary> Aerodynamic/rolling drag. </summary>
        public float[] drag;

        /// <summary>
        /// Animation group from vehicles.ide, e.g. "car", "truck", "bikes", "quad".
        ///
        /// GTA layers its in-vehicle ped animations: the default car set lives in the ped group, and a
        /// class group overrides only what differs. Trucks and buses override entry/exit but still sit with
        /// CAR_sit; bikes replace the set outright, since their ride pose has no seated equivalent. Without
        /// this field every occupant is posed as if sitting in a car, which is visibly wrong on a bike.
        /// </summary>
        public string[] animGroups;

        /// <summary> Vehicle class from vehicles.ide, e.g. "Car", "Bike", "Boat". </summary>
        public string[] vehicleTypes;

        /// <summary>
        /// Raw comp-rules word from vehicles.ide, one per model.
        ///
        /// Packs which "extra" parts a vehicle may wear and the rule for choosing between them. A cargo
        /// truck's advertising boards, a pickup's roll bar and a taxi's roof sign are all extras; the game
        /// picks at most two per vehicle at spawn, which is why two trucks on the same street carry
        /// different advertising.
        /// </summary>
        public int[] compRules;

        public int Count => modelNames != null ? modelNames.Length : 0;

        /// <summary> Index for a model name, or -1. Case-insensitive, as GTA names are inconsistent. </summary>
        public int IndexOf(string modelName)
        {
            if (modelNames == null || string.IsNullOrEmpty(modelName))
                return -1;

            for (int i = 0; i < modelNames.Length; i++)
            {
                if (string.Equals(modelNames[i], modelName, System.StringComparison.OrdinalIgnoreCase))
                    return i;
            }

            return -1;
        }
        /// <summary> Animation group for a model, or an empty string if unknown. </summary>
        public string AnimGroupOf(string modelName)
        {
            int index = IndexOf(modelName);

            if (index < 0 || animGroups == null || index >= animGroups.Length)
                return string.Empty;

            return animGroups[index] ?? string.Empty;
        }
    }
}
