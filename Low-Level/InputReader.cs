using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.InputSystem;

namespace NeKoRoSYS.InputHandling {
    [CreateAssetMenu(fileName = "Input Reader", menuName = "Controls/Input Reader", order = 0)]
    public class InputReader : StaticScriptableObject<InputReader>
    { 
        public bool inGame;
        public Func<bool> IsGamePaused;
        private bool IsPaused() => IsGamePaused?.Invoke() == true;
        private bool ShouldProcessInput() => inGame && !IsPaused();
        // this is an optional function to filter out input if game state is set to paused or if you're not in live gameplay

        public PlayerInputActions playerInputActions;
        public InputAction lookAction;
        public Action<InputSnapshot> OnInputSnapshot;
        public Action<InputDevice, InputDeviceChange> OnDeviceChange;
        public Action<InputButtons, bool> OnButtonInput; 

        private InputButtons currentButtons;
        private InputDevice currentDevice;
        public InputDevice CurrentDevice
        {
            get => currentDevice;
            set {
                currentDevice = value;
                OnDeviceChange?.Invoke(value, InputDeviceChange.UsageChanged);
            }
        }
        
        public bool IsMobile
        {
            get
            {
        #if UNITY_IOS || UNITY_ANDROID
                return true;
        #else
                return false;
        #endif
            }
        }

        private Dictionary<Guid, InputButtons> buttonMap = new();
        private Dictionary<InputButtons, Action<bool>> explicitInputs = new(new InputButtonComparer());
        public void Bind(InputButtons button, Action<bool> callback)
        {
            if (!explicitInputs.ContainsKey(button)) explicitInputs[button] = callback;
            else explicitInputs[button] += callback;
        }

        public void Unbind(InputButtons button, Action<bool> callback)
        {
            if (explicitInputs.ContainsKey(button)) 
            {
                explicitInputs[button] -= callback;
                if (explicitInputs[button] == null) explicitInputs.Remove(button);
            }
        }

        #region Event Handling
        protected override void OnEnable()
        {
            base.OnEnable();
            playerInputActions = new PlayerInputActions();
            
            CacheInputMappings();
            playerInputActions.Default.Look.performed += ProcessLook;
            playerInputActions.Default.Look.canceled += ProcessLook;
            playerInputActions.Default.Move.started += ProcessMove;
            playerInputActions.Default.Move.performed += ProcessMove;
            playerInputActions.Default.Move.canceled += ProcessMove;
            lookAction = playerInputActions.Default.Look;

            foreach (var action in playerInputActions.asset)
            {
                if (action.name == "Move" || action.name == "Look") continue;
                action.performed += HandleAction;
                action.canceled += HandleAction;
            }
            
            ToggleInputs(true);
            InputSystem.settings.SetInternalFeatureFlag("USE_OPTIMIZED_CONTROLS", true);
            InputSystem.settings.SetInternalFeatureFlag("USE_READ_VALUE_CACHING", true);
            InputSystem.settings.SetInternalFeatureFlag("PARANOID_READ_VALUE_CACHING_CHECKS", true);
            InputSystem.onActionChange += HandleActionChange;
        }

        protected override void OnDisable()
        {
            base.OnDisable();
            if (playerInputActions == null) return;
            InputSystem.onActionChange -= HandleActionChange;
            playerInputActions.Default.Look.performed -= ProcessLook;
            playerInputActions.Default.Look.canceled -= ProcessLook;
            playerInputActions.Default.Move.started -= ProcessMove;
            playerInputActions.Default.Move.performed -= ProcessMove;
            playerInputActions.Default.Move.canceled -= ProcessMove;

            foreach (var action in playerInputActions.asset)
            {
                if (action.name == "Move" || action.name == "Look") continue;
                action.performed -= HandleAction;
                action.canceled -= HandleAction;
            }
            ToggleInputs(false);
            DestroyImmediate(playerInputActions.asset);
            playerInputActions = null;
        }

        private void HandleActionChange(object obj, InputActionChange change) {
            if (change == InputActionChange.BoundControlsChanged) CacheInputMappings();
        }

        public void ToggleInputs(bool enable)
        {
            if (enable) playerInputActions.Default.Enable();
            else { playerInputActions.Default.Disable(); ClearAllInputs(); }
        }

        private void ClearAllInputs()
        {
            currentButtons = InputButtons.None;
            persistentButtons = InputButtons.None;
            moveInput = Vector2.zero;
            activeBuffers = 0;
            Array.Clear(inputBufferTimers, 0, inputBufferTimers.Length);
            MarkDirty();
            FlushSnapshot(false); 
        }

        public void ClearAllExplicitBindings() => explicitInputs.Clear();
        #endregion

        #region Dynamic Routing
        private void CacheInputMappings()
        {
            buttonMap.Clear();
            foreach (var action in playerInputActions.asset)
            {
                if (Enum.TryParse(action.name, out InputButtons buttonMapping)) buttonMap[action.id] = buttonMapping;
            }
        }

        private void HandleAction(InputAction.CallbackContext ctx)
        {
            if (!ShouldProcessInput()) return;
            if (buttonMap.TryGetValue(ctx.action.id, out InputButtons button))
            {
                if (ctx.performed || ctx.canceled)
                {
                    bool isPressed = ctx.ReadValueAsButton();
                    SetButtonState(button, isPressed);
                    OnButtonInput?.Invoke(button, isPressed);
                    if (explicitInputs.TryGetValue(button, out var inputEvent)) inputEvent?.Invoke(isPressed);
                    if (isPressed && NeedsBuffering(button)) BufferInput(button);
                }
            }
        }

        private const InputButtons BufferedButtonsMask = InputButtons.Jump | InputButtons.Fire;
        private bool NeedsBuffering(InputButtons button) => (BufferedButtonsMask & button) != 0;
        #endregion

        #region Input Buffering
        private float[] inputBufferTimers = new float[32];
        private uint activeBuffers = 0;
        private const float inputBufferDuration = 0.2f;

        private static readonly int[] DeBruijnPositions = 
        {
            0, 1, 28, 2, 29, 14, 24, 3, 30, 22, 20, 15, 25, 17, 4, 8,
            31, 27, 13, 23, 21, 19, 16, 7, 26, 12, 18, 6, 11, 5, 10, 9
        };

        private int GetButtonIndex(uint bitFlag) => DeBruijnPositions[((bitFlag & (uint)-(int)bitFlag) * 0x077CB531U) >> 27];
        public void BufferInput(InputButtons button)
        {
            uint remainingButtons = (uint)button;
            while (remainingButtons != 0)
            {
                uint singleButtonMask = remainingButtons & (uint)-(int)remainingButtons; 
                int index = GetButtonIndex(singleButtonMask);
                inputBufferTimers[index] = inputBufferDuration;
                activeBuffers |= singleButtonMask; 
                remainingButtons &= remainingButtons - 1; 
            }
        }

        public bool ConsumeBufferedInput(InputButtons button)
        {
            uint buttonMask = (uint)button;
            
            if ((activeBuffers & buttonMask) != 0)
            {
                int index = GetButtonIndex(buttonMask);
                if (inputBufferTimers[index] > 0f)
                {
                    inputBufferTimers[index] = 0f;
                    activeBuffers &= ~buttonMask; 
                    MarkDirty();
                    return true;
                }
            }
            return false;
        }

        public void TickInputBuffers(float deltaTime)
        {
            if (activeBuffers == 0) return; 
            uint remainingBuffers = activeBuffers;
            while (remainingBuffers != 0)
            {
                uint buttonMask = remainingBuffers & (uint)-(int)remainingBuffers;
                int index = GetButtonIndex(buttonMask);
                inputBufferTimers[index] = Math.Max(0, inputBufferTimers[index] - deltaTime);
                if (inputBufferTimers[index] <= 0f)
                {
                    activeBuffers &= ~buttonMask; 
                    MarkDirty();                  
                }
                remainingBuffers &= remainingBuffers - 1; 
            }
        }
        #endregion

        #region Vector Inputs
        public Vector2 moveInput, lookInput;
        public Action<Vector2> OnMoveInput, OnLookInput;

        private void ProcessMove(InputAction.CallbackContext ctx)
        {
            Vector2 newValue = ctx.performed ? ctx.ReadValue<Vector2>() : Vector2.zero;
            if (moveInput != newValue) 
            {
                moveInput = newValue;
                OnMoveInput?.Invoke(moveInput);
                MarkDirty();
            }
        }

        private void ProcessLook(InputAction.CallbackContext ctx) => MarkDirty();
        #endregion

        #region State Management
        private InputButtons persistentButtons;
        private bool snapshotDirty;
        private void MarkDirty() => snapshotDirty = true;
        
        private void SetButtonState(InputButtons button, bool pressed)
        {
            if (pressed) { currentButtons |= button; persistentButtons |= button; }
            else  currentButtons &= ~button;
            MarkDirty();
        }

        public void FlushSnapshot(bool force)
        {
            lookInput = lookAction.ReadValue<Vector2>(); 
            if (!snapshotDirty && !force) return;
            if ((activeBuffers & (uint)InputButtons.Jump) != 0) persistentButtons |= InputButtons.JumpBuffered;
            else persistentButtons &= ~InputButtons.JumpBuffered;
            if ((activeBuffers & (uint)InputButtons.Fire) != 0) persistentButtons |= InputButtons.FireBuffered;
            else persistentButtons &= ~InputButtons.FireBuffered;
            OnLookInput?.Invoke(lookInput);
            OnInputSnapshot?.Invoke(new InputSnapshot
            {
                moveX = (sbyte)(moveInput.x * 127f),
                moveY = (sbyte)(moveInput.y * 127f),
                lookX = (short)(lookInput.x * 100f),
                lookY = (short)(lookInput.y * 100f),
                buttons = persistentButtons
            });
            lookInput = Vector2.zero;
            bool requiresReleaseFlush = (persistentButtons & ~(InputButtons.JumpBuffered | InputButtons.FireBuffered)) != currentButtons;
            persistentButtons = currentButtons; 
            snapshotDirty = requiresReleaseFlush;
        }
        #endregion
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct InputSnapshot : IDisposable
    {
        public sbyte moveX; 
        public sbyte moveY; 
        public short lookX; 
        public short lookY; 
        public InputButtons buttons;
        public readonly Vector2 GetMove() => new(moveX * 0.00787401574f, moveY * 0.00787401574f);
        public readonly Vector2 GetLook() => new(lookX * 0.01f, lookY * 0.01f);
        public readonly bool IsJumpPressed => (buttons & (InputButtons.Jump | InputButtons.JumpBuffered)) != 0;
        public readonly bool IsSprinting => (buttons & InputButtons.Sprint) != 0;
        public readonly bool IsCrouching => (buttons & InputButtons.Crouch) != 0;
        public readonly bool IsHeld(InputButtons button) => (buttons & button) != 0;
        public void Dispose() {}
    }

    [Flags]
    public enum InputButtons : uint
    {
        None       = 0,
        Jump       = 1 << 0,
        Sprint     = 1 << 1,
        Crouch     = 1 << 2,
        Interact   = 1 << 3,
        Primary    = 1 << 4,
        Secondary  = 1 << 5,
        Melee      = 1 << 6,
        DropWeapon = 1 << 7,
        Fire       = 1 << 8,
        AltFire    = 1 << 9,
        Reload     = 1 << 10,
        Pause      = 1 << 11,
        JumpBuffered = 1u << 30,
        FireBuffered = 1u << 31
    }

    public struct InputButtonComparer : IEqualityComparer<InputButtons>
    {
        public bool Equals(InputButtons x, InputButtons y) => x == y;
        public int GetHashCode(InputButtons obj) => (int)obj;
    }
}
