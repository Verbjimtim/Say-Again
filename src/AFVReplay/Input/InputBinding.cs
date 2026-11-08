using System;
using Newtonsoft.Json;
using SharpDX.DirectInput;

namespace AFVReplay.Input
{
    /// <summary>
    /// Our own reading of vatSys's InputMap JSON (same format vatSys stores PTT in),
    /// so the plugin doesn't depend on vatSys's internal class at runtime.
    /// </summary>
    internal sealed class InputBinding
    {
        public enum Kind { Keyboard = 0, Mouse = 1, Joystick = 2 }

        private const int MouseButtonOffset = 12;
        private const int JoystickButtonOffset = 48;

        [JsonProperty("InputType")] public Kind InputType { get; set; }
        [JsonProperty("Key")] public Key Key { get; set; }
        [JsonProperty("JoystickButton")] public JoystickOffset JoystickButton { get; set; }
        [JsonProperty("MouseButton")] public MouseOffset MouseButton { get; set; }
        [JsonProperty("DeviceGuid")] public Guid DeviceGuid { get; set; }

        public bool IsNone => InputType == Kind.Keyboard && Key == Key.Unknown;

        public static InputBinding Parse(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try
            {
                var b = JsonConvert.DeserializeObject<InputBinding>(json);
                return b == null || b.IsNone ? null : b;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>Same wording vatSys uses on its keybind buttons.</summary>
        public override string ToString()
        {
            if (IsNone) return "";
            switch (InputType)
            {
                case Kind.Keyboard: return Key.ToString();
                case Kind.Mouse: return "Mouse: Button " + ((int)MouseButton - MouseButtonOffset);
                case Kind.Joystick: return "Joy: Button " + ((int)JoystickButton - JoystickButtonOffset);
                default: return "";
            }
        }

        public static string Describe(string json) => Parse(json)?.ToString() ?? "";
    }
}
