namespace LiveCaptionsTranslator.utils
{
    // One lock state shared by the floating windows (session only). While locked the windows can't be moved or
    // resized and the mouse goes through them to whatever is underneath.
    public static class FloatLock
    {
        private static bool locked;

        public static event Action? Changed;

        public static bool Locked
        {
            get => locked;
            set
            {
                if (locked == value)
                    return;
                locked = value;
                Changed?.Invoke();
            }
        }
    }
}
