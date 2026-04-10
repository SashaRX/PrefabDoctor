using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace SashaRX.PrefabDoctor
{
    /// <summary>
    /// Determines whether two PropertyModification values represent
    /// a meaningful difference or just floating-point noise.
    /// </summary>
    internal abstract class PropertyComparer
    {
        /// <summary>
        /// Returns true if the two string values are effectively equal
        /// (i.e. the override is insignificant).
        /// </summary>
        public abstract bool AreEffectivelyEqual(string valueA, string valueB);
    }

    /// <summary>
    /// Compares float values with absolute + relative epsilon.
    /// Handles near-zero position drift, scale ~1.0 noise, etc.
    /// </summary>
    internal class FloatComparer : PropertyComparer
    {
        public float AbsEpsilon;
        public float RelEpsilon;

        public FloatComparer(float absEpsilon = 1e-5f, float relEpsilon = 1e-5f)
        {
            AbsEpsilon = absEpsilon;
            RelEpsilon = relEpsilon;
        }

        public override bool AreEffectivelyEqual(string valueA, string valueB)
        {
            if (valueA == valueB) return true;
            if (!TryParseFloat(valueA, out float a) || !TryParseFloat(valueB, out float b))
                return false;

            float diff = Mathf.Abs(a - b);
            if (diff < AbsEpsilon) return true;

            float maxAbs = Mathf.Max(Mathf.Abs(a), Mathf.Abs(b));
            return maxAbs > 0f && diff / maxAbs < RelEpsilon;
        }

        public static bool TryParseFloat(string s, out float result) =>
            float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out result);
    }

    /// <summary>
    /// Compares quaternion rotations accounting for q == -q equivalence.
    /// Must collect all 4 components before comparing — call Collect() for each
    /// component, then CompareCollected() when all 4 are in.
    /// </summary>
    internal class QuaternionComparer
    {
        public float DotThreshold;

        private readonly Dictionary<string, float[]> _pendingA = new(); // key=base path → [x,y,z,w]
        private readonly Dictionary<string, float[]> _pendingB = new();

        public QuaternionComparer(float angleDegreeThreshold = 0.01f)
        {
            // Dot(q1,q2) = cos(halfAngle) → threshold = cos(deg2rad(threshold)/2)
            DotThreshold = Mathf.Cos(Mathf.Deg2Rad * angleDegreeThreshold * 0.5f);
        }

        /// <summary>
        /// Feed one component. basePath = "m_LocalRotation", suffix = ".x"/".y"/".z"/".w".
        /// Returns (ready, equal) — ready=true when all 4 components collected.
        /// </summary>
        public (bool ready, bool equal) Collect(string basePath, string suffix,
            string valueA, string valueB)
        {
            int idx = suffix switch { ".x" => 0, ".y" => 1, ".z" => 2, ".w" => 3, _ => -1 };
            if (idx < 0) return (false, false);

            if (!FloatComparer.TryParseFloat(valueA, out float a) ||
                !FloatComparer.TryParseFloat(valueB, out float b))
                return (false, false);

            if (!_pendingA.ContainsKey(basePath))
            {
                _pendingA[basePath] = new float[4];
                _pendingB[basePath] = new float[4];
            }

            _pendingA[basePath][idx] = a;
            _pendingB[basePath][idx] = b;

            // Check if all 4 components are collected (simple: check if this is .w)
            // In practice we may receive them out of order, so count filled slots
            // For simplicity, only evaluate when .w arrives (it's always last in Unity serialization)
            if (idx != 3) return (false, false);

            var qa = new Quaternion(_pendingA[basePath][0], _pendingA[basePath][1],
                _pendingA[basePath][2], _pendingA[basePath][3]);
            var qb = new Quaternion(_pendingB[basePath][0], _pendingB[basePath][1],
                _pendingB[basePath][2], _pendingB[basePath][3]);

            _pendingA.Remove(basePath);
            _pendingB.Remove(basePath);

            float dot = Mathf.Abs(Quaternion.Dot(qa, qb));
            return (true, dot >= DotThreshold);
        }

        public void Reset()
        {
            _pendingA.Clear();
            _pendingB.Clear();
        }
    }

    /// <summary>
    /// Compares Euler angles with wrap-around normalization.
    /// 0° == 360°, 180° == -180°, etc.
    /// </summary>
    internal class EulerComparer : PropertyComparer
    {
        public float DegreeEpsilon;

        public EulerComparer(float degreeEpsilon = 0.01f)
        {
            DegreeEpsilon = degreeEpsilon;
        }

        public override bool AreEffectivelyEqual(string valueA, string valueB)
        {
            if (valueA == valueB) return true;
            if (!FloatComparer.TryParseFloat(valueA, out float a) ||
                !FloatComparer.TryParseFloat(valueB, out float b))
                return false;

            // Normalize to [0, 360)
            a = NormalizeAngle(a);
            b = NormalizeAngle(b);

            float diff = Mathf.Abs(a - b);
            // Handle wrap-around: diff near 360 means they're close
            if (diff > 180f) diff = 360f - diff;

            return diff < DegreeEpsilon;
        }

        private static float NormalizeAngle(float angle)
        {
            angle %= 360f;
            if (angle < 0f) angle += 360f;
            return angle;
        }
    }

    /// <summary>
    /// Fallback: plain string comparison.
    /// </summary>
    internal class StringComparer : PropertyComparer
    {
        public override bool AreEffectivelyEqual(string valueA, string valueB) =>
            valueA == valueB;
    }

    /// <summary>
    /// Routes property paths to the appropriate comparer.
    /// Reads epsilon values from PrefabDoctorSettings.
    /// </summary>
    internal static class ComparerRouter
    {
        private static FloatComparer s_Float;
        private static FloatComparer s_FloatLoose;
        private static EulerComparer s_Euler;
        private static readonly StringComparer s_String = new();
        private static bool s_Initialized;

        private static void EnsureInitialized()
        {
            if (s_Initialized) return;
            var settings = PrefabDoctorSettings.GetOrCreateDefault();
            s_Float = new FloatComparer(settings.PositionEpsilon, settings.RelativeEpsilon);
            s_FloatLoose = new FloatComparer(settings.GenericFloatEpsilon, settings.GenericFloatEpsilon);
            s_Euler = new EulerComparer(settings.EulerAngleThreshold);
            s_Initialized = true;
        }

        /// <summary>
        /// Force re-read settings (call after user changes settings asset).
        /// </summary>
        public static void ReloadSettings() => s_Initialized = false;

        /// <summary>
        /// Get the right comparer for a property path.
        /// Quaternion comparison is handled separately via QuaternionComparer.
        /// </summary>
        public static PropertyComparer GetComparer(string propertyPath)
        {
            EnsureInitialized();
            // Transform floats
            if (propertyPath.StartsWith("m_LocalPosition.") ||
                propertyPath.StartsWith("m_LocalScale.") ||
                propertyPath.StartsWith("m_AnchoredPosition.") ||
                propertyPath.StartsWith("m_SizeDelta."))
                return s_Float;

            // Euler hints
            if (propertyPath.StartsWith("m_LocalEulerAnglesHint."))
                return s_Euler;

            // Color channels
            if (propertyPath.Contains("m_Color.") ||
                propertyPath.Contains("color.") ||
                propertyPath.Contains("Color."))
                return s_Float;

            // Generic float-like properties (heuristic: ends with .x/.y/.z/.w/.r/.g/.b/.a)
            if (propertyPath.Length > 2)
            {
                string tail = propertyPath[^2..];
                if (tail is ".x" or ".y" or ".z" or ".w" or ".r" or ".g" or ".b" or ".a")
                {
                    // Check if value is actually a float (will be validated during comparison)
                    return s_FloatLoose;
                }
            }

            return s_String;
        }

        /// <summary>
        /// Returns true if this property path is part of a quaternion
        /// and should use QuaternionComparer instead.
        /// </summary>
        public static bool IsQuaternionComponent(string propertyPath) =>
            propertyPath.StartsWith("m_LocalRotation.");

        /// <summary>
        /// Extract base path and suffix for quaternion grouping.
        /// "m_LocalRotation.x" → ("m_LocalRotation", ".x")
        /// </summary>
        public static (string basePath, string suffix) SplitQuaternionPath(string propertyPath)
        {
            int lastDot = propertyPath.LastIndexOf('.');
            if (lastDot < 0) return (propertyPath, "");
            return (propertyPath[..lastDot], propertyPath[lastDot..]);
        }
    }
}
