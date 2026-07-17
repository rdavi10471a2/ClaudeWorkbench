using System.Runtime.InteropServices;

namespace AIMonitor.Runtime;

public static class PreMergeValidationOverridePrompt
{
    public static bool CanShow()
    {
        return OperatingSystem.IsWindows()
            && Environment.UserInteractive
            && !string.Equals(Environment.GetEnvironmentVariable("AIMONITOR_DISABLE_VALIDATION_DIALOG"), "1", StringComparison.Ordinal);
    }

    public static bool Prompt(IReadOnlyList<string> diagnostics)
    {
        string diagnosticText = diagnostics.Count == 0
            ? "No error diagnostics were captured."
            : string.Join(Environment.NewLine, diagnostics.Take(8));
        string dialogContent = diagnosticText + Environment.NewLine + Environment.NewLine
            + "Open WinMerge anyway?";
        if (TryPromptWithTaskDialog(dialogContent, out bool launchApproved))
        {
            return launchApproved;
        }

        string message = "Pre-merge validation failed." + Environment.NewLine + Environment.NewLine
            + dialogContent;
        int result = MessageBoxW(
            IntPtr.Zero,
            message,
            "AIMonitor Pre-Merge Validation",
            MessageBoxTypeYesNo | MessageBoxIconWarning | MessageBoxDefaultButton2 | MessageBoxSetForeground);
        return result == MessageBoxResultYes;
    }

    private static bool TryPromptWithTaskDialog(string message, out bool launchApproved)
    {
        launchApproved = false;
        IntPtr buttonsPointer = IntPtr.Zero;

        try
        {
            TaskDialogButton[] buttons =
            [
                new()
                {
                    ButtonId = TaskDialogButtonLaunch,
                    ButtonText = "Yes Launch"
                },
                new()
                {
                    ButtonId = TaskDialogButtonCancel,
                    ButtonText = "Cancel"
                }
            ];

            int buttonSize = Marshal.SizeOf<TaskDialogButton>();
            buttonsPointer = Marshal.AllocHGlobal(buttonSize * buttons.Length);
            for (int index = 0; index < buttons.Length; index++)
            {
                Marshal.StructureToPtr(buttons[index], buttonsPointer + (index * buttonSize), fDeleteOld: false);
            }

            TaskDialogConfig config = new()
            {
                Size = (uint)Marshal.SizeOf<TaskDialogConfig>(),
                Flags = TaskDialogAllowDialogCancellation | TaskDialogPositionRelativeToWindow,
                WindowTitle = "AIMonitor Pre-Merge Validation",
                MainIcon = TaskDialogWarningIcon,
                MainInstruction = "Pre-merge validation failed.",
                Content = message,
                ButtonCount = (uint)buttons.Length,
                Buttons = buttonsPointer,
                DefaultButton = TaskDialogButtonCancel
            };

            int hr = TaskDialogIndirect(ref config, out int selectedButton, out _, out _);
            if (hr != 0)
            {
                return false;
            }

            launchApproved = selectedButton == TaskDialogButtonLaunch;
            return true;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
        catch (DllNotFoundException)
        {
            return false;
        }
        finally
        {
            if (buttonsPointer != IntPtr.Zero)
            {
                int buttonSize = Marshal.SizeOf<TaskDialogButton>();
                for (int index = 0; index < 2; index++)
                {
                    Marshal.DestroyStructure<TaskDialogButton>(buttonsPointer + (index * buttonSize));
                }

                Marshal.FreeHGlobal(buttonsPointer);
            }
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = false)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

    [DllImport("comctl32.dll", CharSet = CharSet.Unicode, SetLastError = false)]
    private static extern int TaskDialogIndirect(
        ref TaskDialogConfig taskConfig,
        out int button,
        out int radioButton,
        [MarshalAs(UnmanagedType.Bool)] out bool verificationFlagChecked);

    private const uint MessageBoxTypeYesNo = 0x00000004;
    private const uint MessageBoxIconWarning = 0x00000030;
    private const uint MessageBoxDefaultButton2 = 0x00000100;
    private const uint MessageBoxSetForeground = 0x00010000;
    private const int MessageBoxResultYes = 6;
    private const int TaskDialogButtonLaunch = 1001;
    private const int TaskDialogButtonCancel = 2;
    private const uint TaskDialogAllowDialogCancellation = 0x00000008;
    private const uint TaskDialogPositionRelativeToWindow = 0x00001000;
    private static readonly IntPtr TaskDialogWarningIcon = new(ushort.MaxValue);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct TaskDialogButton
    {
        public int ButtonId;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string ButtonText;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct TaskDialogConfig
    {
        public uint Size;
        public IntPtr ParentWindowHandle;
        public IntPtr InstanceHandle;
        public uint Flags;
        public uint CommonButtons;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string WindowTitle;

        public IntPtr MainIcon;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string MainInstruction;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string Content;

        public uint ButtonCount;
        public IntPtr Buttons;
        public int DefaultButton;
        public uint RadioButtonCount;
        public IntPtr RadioButtons;
        public int DefaultRadioButton;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string? VerificationText;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string? ExpandedInformation;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string? ExpandedControlText;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string? CollapsedControlText;

        public IntPtr FooterIcon;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string? Footer;

        public IntPtr Callback;
        public IntPtr CallbackData;
        public uint Width;
    }
}
