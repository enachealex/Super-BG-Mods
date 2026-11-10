using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace SBGSplitscreen
{
    /// <summary>
    /// The HUD pieces the server drives (the tee-off "3, 2, 1, Golf!", the match-end and
    /// next-match countdowns, the knock-out feed) exist once, in player 1's HUD, and a copy of
    /// them cannot take part in networking. Joined players get a look-alike instead: the copy has
    /// its scripts removed and, every frame, takes on what player 1's original shows (text,
    /// colours, fades, animation pose, feed entries coming and going).
    /// </summary>
    public sealed class HudMirror : MonoBehaviour
    {
        /// <summary>Path of the original under player 1's gameplay canvas.</summary>
        public string Path;

        private Transform source;
        private static GameObject stagingArea;

        /// <summary>Turns a networked HUD piece inside a HUD copy (not yet active) into a look-alike of player 1's.</summary>
        public static void Attach(GameObject copy, string path)
        {
            StripLogic(copy);
            copy.AddComponent<HudMirror>().Path = path;
            // Networked scene objects stay switched off until the server spawns them, and the HUD
            // may have been copied before that. The look-alike stays on so that it keeps updating;
            // while the original is hidden, its contents are switched off instead.
            copy.SetActive(true);
        }

        /// <summary>Removes everything that would run on its own (scripts, animators, the network identity); graphics and masks stay.</summary>
        private static void StripLogic(GameObject root)
        {
            var doomed = new List<Behaviour>();
            foreach (var behaviour in root.GetComponentsInChildren<Behaviour>(true))
            {
                if (behaviour != null && !(behaviour is HudMirror) && !IsVisual(behaviour))
                {
                    doomed.Add(behaviour);
                }
            }
            // Network behaviours require their identity, so the identity goes last.
            foreach (var behaviour in doomed)
            {
                if (!(behaviour is Mirror.NetworkIdentity))
                {
                    Object.DestroyImmediate(behaviour);
                }
            }
            foreach (var behaviour in doomed)
            {
                if (behaviour != null)
                {
                    Object.DestroyImmediate(behaviour);
                }
            }
        }

        private static bool IsVisual(Behaviour behaviour)
        {
            return behaviour is Graphic || behaviour is Canvas || behaviour is CanvasGroup || behaviour is Mask || behaviour is RectMask2D
                || behaviour is BaseMeshEffect;
        }

        private void LateUpdate()
        {
            if (source == null)
            {
                var canvas = PlayerHuds.PlayerOneCanvas();
                source = canvas != null ? canvas.transform.Find(Path) : null;
                if (source == null)
                {
                    return;
                }
            }
            // This object itself stays switched on so that it keeps updating; when the original is
            // hidden, its contents are switched off instead.
            if (!source.gameObject.activeInHierarchy)
            {
                for (int i = 0; i < transform.childCount; i++)
                {
                    transform.GetChild(i).gameObject.SetActive(false);
                }
                var graphic = GetComponent<Graphic>();
                if (graphic != null)
                {
                    graphic.enabled = false;
                }
                return;
            }
            Sync(source, transform);
        }

        private static void Sync(Transform from, Transform to)
        {
            if (from is RectTransform rf && to is RectTransform rt)
            {
                if (rt.anchorMin != rf.anchorMin) rt.anchorMin = rf.anchorMin;
                if (rt.anchorMax != rf.anchorMax) rt.anchorMax = rf.anchorMax;
                if (rt.pivot != rf.pivot) rt.pivot = rf.pivot;
                if (rt.anchoredPosition != rf.anchoredPosition) rt.anchoredPosition = rf.anchoredPosition;
                if (rt.sizeDelta != rf.sizeDelta) rt.sizeDelta = rf.sizeDelta;
            }
            if (to.localScale != from.localScale) to.localScale = from.localScale;
            if (to.localRotation != from.localRotation) to.localRotation = from.localRotation;

            var groupFrom = from.GetComponent<CanvasGroup>();
            var groupTo = to.GetComponent<CanvasGroup>();
            if (groupFrom != null && groupTo != null && groupTo.alpha != groupFrom.alpha)
            {
                groupTo.alpha = groupFrom.alpha;
            }

            var graphicFrom = from.GetComponent<Graphic>();
            var graphicTo = to.GetComponent<Graphic>();
            if (graphicFrom != null && graphicTo != null)
            {
                if (graphicTo.enabled != graphicFrom.enabled) graphicTo.enabled = graphicFrom.enabled;
                if (graphicTo.color != graphicFrom.color) graphicTo.color = graphicFrom.color;
                if (graphicFrom is TMP_Text textFrom && graphicTo is TMP_Text textTo)
                {
                    if (textTo.text != textFrom.text) textTo.text = textFrom.text;
                }
                else if (graphicFrom is Image imageFrom && graphicTo is Image imageTo)
                {
                    if (imageTo.sprite != imageFrom.sprite) imageTo.sprite = imageFrom.sprite;
                    if (imageTo.fillAmount != imageFrom.fillAmount) imageTo.fillAmount = imageFrom.fillAmount;
                }
            }

            // Children by position: entries the original gained (feed lines) are copied in, ones it lost are dropped.
            int count = from.childCount;
            for (int i = 0; i < count; i++)
            {
                var childFrom = from.GetChild(i);
                var childTo = i < to.childCount ? to.GetChild(i) : null;
                if (childTo == null || childTo.name != childFrom.name)
                {
                    if (childTo != null)
                    {
                        Discard(childTo);
                    }
                    childTo = CopyIn(childFrom, to, i);
                }
                bool active = childFrom.gameObject.activeSelf;
                if (childTo.gameObject.activeSelf != active)
                {
                    childTo.gameObject.SetActive(active);
                }
                if (active)
                {
                    Sync(childFrom, childTo);
                }
            }
            while (to.childCount > count)
            {
                Discard(to.GetChild(to.childCount - 1));
            }
        }

        /// <summary>Copies an element the original gained. It is copied while switched off so none of its scripts start before they are removed.</summary>
        private static Transform CopyIn(Transform original, Transform parent, int index)
        {
            if (stagingArea == null)
            {
                stagingArea = new GameObject("Splitscreen HUD mirror staging");
                stagingArea.SetActive(false);
                Object.DontDestroyOnLoad(stagingArea);
            }
            var copy = Object.Instantiate(original.gameObject, stagingArea.transform, false);
            copy.name = original.name;
            StripLogic(copy);
            copy.transform.SetParent(parent, false);
            copy.transform.SetSiblingIndex(index);
            return copy.transform;
        }

        private static void Discard(Transform child)
        {
            child.SetParent(null, false);
            Object.Destroy(child.gameObject);
        }
    }
}
