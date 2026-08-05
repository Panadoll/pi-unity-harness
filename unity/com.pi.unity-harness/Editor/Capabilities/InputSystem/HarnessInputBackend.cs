using System;
using System.Collections.Generic;
using Pi.UnityHarness.Editor.Capabilities.Shared;
using UnityEditor;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using UnityEngine.InputSystem.LowLevel;

namespace Pi.UnityHarness.Editor.Capabilities.Input
{
    public static class HarnessInputBackend
    {
        private const string SyntheticMouseName = "HarnessSyntheticMouse";
        private const string SyntheticKeyboardName = "HarnessSyntheticKeyboard";

        private static readonly HashSet<Key> s_heldKeys = new HashSet<Key>();
        private static readonly HashSet<int> s_heldMouseButtons = new HashSet<int>();
        private static bool s_inputUpdateHooked;
        private static bool s_inputRouteConfigured;
        private static Type s_gameViewType;
        private static Vector2 s_mousePosition;
        private static Mouse s_syntheticMouse;
        private static Keyboard s_syntheticKeyboard;
        private static GameObject s_pointerPress;
        private static GameObject s_rawPointerPress;
        private static GameObject s_pointerDrag;
        private static GameObject s_currentPointerTarget;
        private static Vector2 s_pressPosition;
        private static bool s_dragging;
        private static bool s_eventSystemOwnsPointer;

        /// <summary>Most recent EventSystem dispatch result summary.</summary>
        internal struct DispatchResult
        {
            public bool Attempted;
            public bool Dispatched;
            public string TopHitName;
            public string HandlerName;
            public string Error;
        }

        private static DispatchResult s_lastDispatchResult;

        internal static DispatchResult LastDispatchResult => s_lastDispatchResult;

        private static Type GameViewType => s_gameViewType ??=
            typeof(EditorWindow).Assembly.GetType("UnityEditor.GameView");

        private static bool HasSyntheticInput =>
            s_heldKeys.Count > 0 || s_heldMouseButtons.Count > 0;

        [InitializeOnLoad]
        private static class ReloadWatcher
        {
            static ReloadWatcher()
            {
                s_heldKeys.Clear();
                s_heldMouseButtons.Clear();
                s_inputUpdateHooked = false;
                s_inputRouteConfigured = false;
                s_mousePosition = Vector2.zero;
                s_eventSystemOwnsPointer = false;
                ClearPointerState();
            }
        }

        public static void PressKey(string key) => SetKeyState(ParseKey(key), true);

        public static void ReleaseKey(string key) => SetKeyState(ParseKey(key), false);

        public static void PressMouseButton(int button) => SetMouseButtonState(button, true);

        public static void ReleaseMouseButton(int button) => SetMouseButtonState(button, false);

        public static void SetMousePosition(float screenshotX, float screenshotY)
        {
            EnsureInputRoutesToGameView();
            EnsureInputUpdateHook();
            FocusGameView();

            var mouse = EnsureSyntheticMouse();
            float inputY = ConvertScreenshotY(screenshotY);
            s_mousePosition = new Vector2(screenshotX, inputY);
            bool useEventSystemDispatch = ShouldUseEventSystemFallback();
            if (!s_eventSystemOwnsPointer)
                ApplyMouseState(mouse, Vector2.zero);
            if (useEventSystemDispatch)
                DispatchPointerMoveOrDrag();
        }

        public static void ClearAllInput()
        {
            s_heldKeys.Clear();
            s_heldMouseButtons.Clear();
            s_eventSystemOwnsPointer = false;
            ClearPointerState();
            InputSystem.onAfterUpdate += OneTimeClear;
        }

        public static bool HasKeyboard()
        {
            if (Keyboard.current != null || GetSyntheticKeyboardIfAvailable() != null)
                return true;

            if (!EditorApplication.isPlaying)
                return false;

            try
            {
                EnsureSyntheticKeyboard();
                return true;
            }
            catch
            {
                return false;
            }
        }

        public static bool HasMouse()
        {
            if (Mouse.current != null || GetSyntheticMouseIfAvailable() != null)
                return true;

            if (!EditorApplication.isPlaying)
                return false;

            try
            {
                EnsureSyntheticMouse();
                return true;
            }
            catch
            {
                return false;
            }
        }

        public static void PrepareForInput()
        {
            EnsureInputRoutesToGameView();
            EnsureInputUpdateHook();
            EnsureSyntheticMouse();
            EnsureSyntheticKeyboard();
            FocusGameView();
        }

        public static void SetScrollDelta(float deltaX, float deltaY)
        {
            var mouse = Mouse.current;
            if (mouse == null)
                throw new InvalidOperationException("Input System mouse device is not available.");

            EnsureInputRoutesToGameView();
            EnsureInputUpdateHook();

            bool useEventSystemDispatch = ShouldUseEventSystemFallback();
            if (useEventSystemDispatch)
            {
                DispatchScroll(new Vector2(deltaX, deltaY));
            }

            // EventSystem dispatch owns UGUI scrolls; do not feed the same wheel delta twice.
            ApplyMouseState(mouse, new Vector2(deltaX, deltaY),
                useEventSystemDispatch, useEventSystemDispatch);
        }

        public static void QueueTextInput(char c)
        {
            var keyboard = Keyboard.current;
            if (keyboard == null)
                throw new InvalidOperationException("Input System keyboard device is not available.");

            EnsureInputRoutesToGameView();
            EnsureInputUpdateHook();

            var textEvent = TextEvent.Create(keyboard.deviceId, c);
            InputSystem.QueueEvent(ref textEvent);
            DispatchTextInput(c);
        }

        private static void OneTimeClear()
        {
            InputSystem.onAfterUpdate -= OneTimeClear;

            var keyboard = Keyboard.current;
            if (keyboard != null)
            {
                using (StateEvent.From(keyboard, out var eventPtr))
                {
                    foreach (Key key in Enum.GetValues(typeof(Key)))
                    {
                        if (key == Key.None || key == Key.IMESelected) continue;
                        try { keyboard[key].WriteValueIntoEvent(0f, eventPtr); }
                        catch (ArgumentException) { }
                    }
                    InputState.Change(keyboard, eventPtr);
                }
            }

            var mouse = GetSyntheticMouseIfAvailable() ?? Mouse.current;
            if (mouse != null)
            {
                ApplyMouseState(mouse, Vector2.zero);
            }
        }

        private static void SetKeyState(Key key, bool pressed)
        {
            EnsureInputRoutesToGameView();
            EnsureInputUpdateHook();

            var keyboard = EnsureSyntheticKeyboard();
            if (pressed)
            {
                s_heldKeys.Add(key);
            }
            else
            {
                s_heldKeys.Remove(key);
            }

            ApplyKeyboardState(keyboard);
        }

        private static void SetMouseButtonState(int button, bool pressed)
        {
            var mouse = EnsureSyntheticMouse();
            var control = GetMouseButtonControl(mouse, button);
            if (control == null)
                throw new ArgumentException("Invalid mouse button: " + button);

            EnsureInputRoutesToGameView();
            EnsureInputUpdateHook();

            if (pressed)
            {
                s_heldMouseButtons.Add(button);
            }
            else
            {
                s_heldMouseButtons.Remove(button);
            }

            bool useEventSystemDispatch = button == 0 && ShouldUseEventSystemFallback();
            if (useEventSystemDispatch && pressed)
                s_eventSystemOwnsPointer = true;

            try
            {
                if (useEventSystemDispatch)
                    DispatchPointerButton(pressed);

                // EventSystem dispatch owns this UGUI pointer action; avoid duplicate input state.
                ApplyMouseState(mouse, Vector2.zero,
                    useEventSystemDispatch, useEventSystemDispatch);
            }
            finally
            {
                if (useEventSystemDispatch && !pressed)
                    s_eventSystemOwnsPointer = false;
            }
        }

        private static void EnsureInputUpdateHook()
        {
            if (s_inputUpdateHooked) return;
            InputSystem.onBeforeUpdate += OnBeforeInputSystemUpdate;
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
            s_inputUpdateHooked = true;
        }

        private static void OnBeforeInputSystemUpdate()
        {
            if (HasSyntheticInput)
                ReapplySyntheticState();
        }

        private static void ReapplySyntheticState()
        {
            if (s_heldKeys.Count > 0)
            {
                var keyboard = GetSyntheticKeyboardIfAvailable() ?? Keyboard.current;
                if (keyboard != null)
                {
                    ApplyKeyboardState(keyboard);
                }
            }

            if (s_heldMouseButtons.Count > 0)
            {
                if (s_eventSystemOwnsPointer)
                    return;

                var mouse = GetSyntheticMouseIfAvailable() ?? Mouse.current;
                if (mouse != null)
                {
                    ApplyMouseState(mouse, Vector2.zero);
                }
            }
        }

        private static void OnPlayModeChanged(PlayModeStateChange change)
        {
            if (change == PlayModeStateChange.ExitingPlayMode)
            {
                s_heldKeys.Clear();
                s_heldMouseButtons.Clear();
                s_mousePosition = Vector2.zero;
                s_eventSystemOwnsPointer = false;
                ClearPointerState();
            }
        }

        private static void EnsureInputRoutesToGameView()
        {
            if (s_inputRouteConfigured) return;
            var settings = InputSystem.settings;
            if (settings != null)
            {
                settings.editorInputBehaviorInPlayMode =
                    InputSettings.EditorInputBehaviorInPlayMode.AllDeviceInputAlwaysGoesToGameView;
            }
            s_inputRouteConfigured = true;
        }

        private static void FocusGameView()
        {
            if (GameViewType != null)
                EditorWindow.FocusWindowIfItsOpen(GameViewType);
        }

        private static float ConvertScreenshotY(float y)
        {
            return PiGameViewCoordinates.TopLeftYToScreenY(y);
        }

        private static Mouse EnsureSyntheticMouse()
        {
            s_syntheticMouse = GetSyntheticMouseIfAvailable();
            if (s_syntheticMouse == null)
                s_syntheticMouse = InputSystem.AddDevice<Mouse>(SyntheticMouseName);

            s_syntheticMouse.MakeCurrent();
            return s_syntheticMouse;
        }

        private static Keyboard EnsureSyntheticKeyboard()
        {
            s_syntheticKeyboard = GetSyntheticKeyboardIfAvailable();
            if (s_syntheticKeyboard == null)
                s_syntheticKeyboard = InputSystem.AddDevice<Keyboard>(SyntheticKeyboardName);

            s_syntheticKeyboard.MakeCurrent();
            return s_syntheticKeyboard;
        }

        private static Mouse GetSyntheticMouseIfAvailable()
        {
            if (s_syntheticMouse != null && s_syntheticMouse.added)
                return s_syntheticMouse;

            foreach (var device in InputSystem.devices)
            {
                if (device is Mouse mouse && device.name == SyntheticMouseName)
                    return mouse;
            }

            return null;
        }

        private static Keyboard GetSyntheticKeyboardIfAvailable()
        {
            if (s_syntheticKeyboard != null && s_syntheticKeyboard.added)
                return s_syntheticKeyboard;

            foreach (var device in InputSystem.devices)
            {
                if (device is Keyboard keyboard && device.name == SyntheticKeyboardName)
                    return keyboard;
            }

            return null;
        }

        private static void ApplyMouseState(
            Mouse mouse,
            Vector2 scroll,
            bool skipQueueStateEvent = false,
            bool skipInputStateChange = false)
        {
            if (mouse == null) return;

            var state = new MouseState
            {
                position = s_mousePosition,
                scroll = scroll
            };

            foreach (var heldButton in s_heldMouseButtons)
                state = state.WithButton(ToMouseButton(heldButton), true);

            if (!skipInputStateChange)
            {
                using (StateEvent.From(mouse, out var eventPtr))
                {
                    mouse.position.WriteValueIntoEvent(state.position, eventPtr);
                    mouse.scroll.WriteValueIntoEvent(state.scroll, eventPtr);
                    WriteMouseButtonIntoEvent(mouse, 0, s_heldMouseButtons.Contains(0), eventPtr);
                    WriteMouseButtonIntoEvent(mouse, 1, s_heldMouseButtons.Contains(1), eventPtr);
                    WriteMouseButtonIntoEvent(mouse, 2, s_heldMouseButtons.Contains(2), eventPtr);
                    WriteMouseButtonIntoEvent(mouse, 3, s_heldMouseButtons.Contains(3), eventPtr);
                    WriteMouseButtonIntoEvent(mouse, 4, s_heldMouseButtons.Contains(4), eventPtr);
                    InputState.Change(mouse, eventPtr);
                }
            }

            // When EventSystem dispatch already handled the UI action,
            // skip queued state so the same press/release/scroll is not consumed twice.
            if (!skipQueueStateEvent && !skipInputStateChange)
                InputSystem.QueueStateEvent(mouse, state);
        }

        private static void ApplyKeyboardState(Keyboard keyboard)
        {
            if (keyboard == null) return;

            using (StateEvent.From(keyboard, out var eventPtr))
            {
                foreach (Key key in Enum.GetValues(typeof(Key)))
                {
                    if (key == Key.None || key == Key.IMESelected) continue;
                    try
                    {
                        keyboard[key].WriteValueIntoEvent(s_heldKeys.Contains(key) ? 1f : 0f, eventPtr);
                    }
                    catch (ArgumentException)
                    {
                    }
                }

                InputState.Change(keyboard, eventPtr);
                InputSystem.QueueEvent(eventPtr);
            }
        }

        private static void WriteMouseButtonIntoEvent(Mouse mouse, int button, bool pressed, InputEventPtr eventPtr)
        {
            var control = GetMouseButtonControl(mouse, button);
            if (control != null)
                control.WriteValueIntoEvent(pressed ? 1f : 0f, eventPtr);
        }

        private static void DispatchPointerButton(bool pressed)
        {
            var eventSystem = EventSystem.current;
            if (eventSystem == null)
            {
                s_lastDispatchResult = new DispatchResult
                    { Attempted = true, Error = "no_event_system" };
                return;
            }

            var data = CreatePointerData(eventSystem);
            var topHit = data.pointerCurrentRaycast.gameObject;
            string topHitName = topHit != null ? topHit.name : null;

            if (pressed)
            {
                s_currentPointerTarget = topHit;
                s_pressPosition = s_mousePosition;
                data.pressPosition = s_pressPosition;
                data.pointerPressRaycast = data.pointerCurrentRaycast;
                data.eligibleForClick = true;
                data.dragging = false;
                data.useDragThreshold = true;
                data.clickTime = Time.unscaledTime;
                data.clickCount = 1;

                if (s_currentPointerTarget == null)
                {
                    s_lastDispatchResult = new DispatchResult
                    {
                        Attempted = true, Dispatched = false,
                        TopHitName = null, Error = "no_hit"
                    };
                    ClearPointerState();
                    return;
                }

                s_pointerPress = ExecuteEvents.ExecuteHierarchy(
                    s_currentPointerTarget, data, ExecuteEvents.pointerDownHandler);
                if (s_pointerPress == null)
                    s_pointerPress = ExecuteEvents.GetEventHandler<IPointerClickHandler>(s_currentPointerTarget);

                s_rawPointerPress = s_currentPointerTarget;
                s_pointerDrag = ExecuteEvents.GetEventHandler<IDragHandler>(s_currentPointerTarget);
                if (s_pointerDrag != null)
                    ExecuteEvents.Execute(s_pointerDrag, data, ExecuteEvents.initializePotentialDrag);

                eventSystem.SetSelectedGameObject(s_pointerPress ?? s_currentPointerTarget, data);

                s_lastDispatchResult = new DispatchResult
                {
                    Attempted = true, Dispatched = true,
                    TopHitName = topHitName,
                    HandlerName = s_pointerPress != null ? s_pointerPress.name : topHitName
                };
            }
            else
            {
                data.pressPosition = s_pressPosition;
                data.pointerPress = s_pointerPress;
                data.rawPointerPress = s_rawPointerPress;
                data.pointerDrag = s_pointerDrag;
                data.dragging = s_dragging;
                data.eligibleForClick = true;

                if (s_pointerPress != null)
                    ExecuteEvents.Execute(s_pointerPress, data, ExecuteEvents.pointerUpHandler);

                var releaseTarget = data.pointerCurrentRaycast.gameObject;
                if (releaseTarget == null)
                    releaseTarget = s_rawPointerPress;

                var clickHandler = ExecuteEvents.GetEventHandler<IPointerClickHandler>(releaseTarget);
                bool clicked = !s_dragging && s_pointerPress != null && s_pointerPress == clickHandler;
                if (clicked)
                    ExecuteEvents.Execute(s_pointerPress, data, ExecuteEvents.pointerClickHandler);

                if (s_dragging && s_pointerDrag != null)
                {
                    ExecuteEvents.ExecuteHierarchy(releaseTarget, data, ExecuteEvents.dropHandler);
                    ExecuteEvents.Execute(s_pointerDrag, data, ExecuteEvents.endDragHandler);
                }

                s_lastDispatchResult = new DispatchResult
                {
                    Attempted = true,
                    Dispatched = clicked || s_dragging,
                    TopHitName = topHitName,
                    HandlerName = clicked && s_pointerPress != null ? s_pointerPress.name
                        : (s_dragging && s_pointerDrag != null ? s_pointerDrag.name : null),
                    Error = !clicked && !s_dragging ? "no_click_handler_match" : null
                };

                ClearPointerState();
            }
        }

        private static bool ShouldUseEventSystemFallback()
        {
            return EventSystem.current != null;
        }

        private static void DispatchPointerMoveOrDrag()
        {
            var eventSystem = EventSystem.current;
            if (eventSystem == null) return;

            var data = CreatePointerData(eventSystem);
            s_currentPointerTarget = data.pointerCurrentRaycast.gameObject;

            if (s_heldMouseButtons.Contains(0) && s_pointerDrag != null)
            {
                data.pressPosition = s_pressPosition;
                data.pointerPress = s_pointerPress;
                data.rawPointerPress = s_rawPointerPress;
                data.pointerDrag = s_pointerDrag;
                data.dragging = s_dragging;

                if (!s_dragging)
                {
                    ExecuteEvents.Execute(s_pointerDrag, data, ExecuteEvents.beginDragHandler);
                    s_dragging = true;
                    data.dragging = true;
                }

                ExecuteEvents.Execute(s_pointerDrag, data, ExecuteEvents.dragHandler);
                return;
            }

            if (s_currentPointerTarget != null)
                ExecuteEvents.ExecuteHierarchy(s_currentPointerTarget, data, ExecuteEvents.pointerMoveHandler);
        }

        private static void DispatchScroll(Vector2 scroll)
        {
            var eventSystem = EventSystem.current;
            if (eventSystem == null || scroll == Vector2.zero)
            {
                s_lastDispatchResult = new DispatchResult
                {
                    Attempted = true, Dispatched = false,
                    Error = eventSystem == null ? "no_event_system" : "zero_scroll"
                };
                return;
            }

            var data = CreatePointerData(eventSystem);
            data.scrollDelta = scroll;
            var topHit = data.pointerCurrentRaycast.gameObject;

            if (topHit == null)
            {
                s_lastDispatchResult = new DispatchResult
                    { Attempted = true, Dispatched = false, Error = "no_hit" };
                return;
            }

            var handler = ExecuteEvents.ExecuteHierarchy(
                topHit, data, ExecuteEvents.scrollHandler);

            s_lastDispatchResult = new DispatchResult
            {
                Attempted = true,
                Dispatched = handler != null,
                TopHitName = topHit.name,
                HandlerName = handler != null ? handler.name : null,
                Error = handler == null ? "no_scroll_handler" : null
            };
        }

        private static void DispatchTextInput(char c)
        {
            var eventSystem = EventSystem.current;
            var selected = eventSystem != null ? eventSystem.currentSelectedGameObject : null;
            if (selected == null) return;

            if (AppendTextByReflection(selected, "TMPro.TMP_InputField, Unity.TextMeshPro", c))
                return;
            AppendTextByReflection(selected, "UnityEngine.UI.InputField, UnityEngine.UI", c);
        }

        private static bool AppendTextByReflection(GameObject selected, string typeName, char c)
        {
            var type = Type.GetType(typeName);
            if (type == null) return false;

            var component = selected.GetComponent(type);
            if (component == null) return false;

            var textProperty = type.GetProperty("text");
            if (textProperty == null || !textProperty.CanRead || !textProperty.CanWrite)
                return false;

            string currentText = textProperty.GetValue(component, null) as string ?? "";
            textProperty.SetValue(component, currentText + c, null);
            return true;
        }

        private static PointerEventData CreatePointerData(EventSystem eventSystem)
        {
            var data = new PointerEventData(eventSystem)
            {
                pointerId = -1,
                position = s_mousePosition,
                delta = Vector2.zero,
                button = PointerEventData.InputButton.Left,
                clickTime = Time.unscaledTime,
                clickCount = 1
            };

            data.pointerCurrentRaycast = PiUiRaycastHelper.RaycastUI(s_mousePosition, eventSystem);
            return data;
        }

        private static void ClearPointerState()
        {
            s_pointerPress = null;
            s_rawPointerPress = null;
            s_pointerDrag = null;
            s_currentPointerTarget = null;
            s_pressPosition = Vector2.zero;
            s_dragging = false;
        }

        private static Key ParseKey(string key)
        {
            if (string.IsNullOrWhiteSpace(key))
                throw new ArgumentException("Key is required. Use UnityEngine.InputSystem.Key names such as W, Space, or Enter.");

            if (Enum.TryParse(key.Trim(), true, out Key parsed) && parsed != Key.None)
                return parsed;

            throw new ArgumentException("Invalid key: " + key);
        }

        private static ButtonControl GetMouseButtonControl(Mouse mouse, int button)
        {
            if (mouse == null) return null;
            switch (button)
            {
                case 0: return mouse.leftButton;
                case 1: return mouse.rightButton;
                case 2: return mouse.middleButton;
                case 3: return mouse.forwardButton;
                case 4: return mouse.backButton;
                default: return null;
            }
        }

        private static MouseButton ToMouseButton(int button)
        {
            switch (button)
            {
                case 0: return MouseButton.Left;
                case 1: return MouseButton.Right;
                case 2: return MouseButton.Middle;
                case 3: return MouseButton.Forward;
                case 4: return MouseButton.Back;
                default: throw new ArgumentException("Invalid mouse button: " + button);
            }
        }
    }
}
