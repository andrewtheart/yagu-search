using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Text;
using TextControlBoxNS.Core;
using Windows.ApplicationModel.DataTransfer;

namespace TextControlBoxNS.Helper
{
    internal class FlyoutHelper
    {
        public MenuFlyout menuFlyout;

        public void Init(CoreTextControlBox sender)
        {
            CreateFlyout(sender);
        }

        public void CreateFlyout(CoreTextControlBox sender)
        {
            menuFlyout = new MenuFlyout();
            menuFlyout.Items.Add(CreateItem(() => { CopyWithLineNumbers(sender); }, "Copy (with line numbers)", Symbol.Copy, ""));
            menuFlyout.Items.Add(CreateItem(() => { sender.Copy(); }, "Copy (without line numbers)", Symbol.Copy, ""));
            menuFlyout.Items.Add(CreateItem(() => { sender.Paste(); }, "Paste", Symbol.Paste, ""));
            menuFlyout.Items.Add(CreateItem(() => { sender.Cut(); }, "Cut", Symbol.Cut, ""));
            menuFlyout.Items.Add(new MenuFlyoutSeparator());
            menuFlyout.Items.Add(CreateItem(() => { sender.Undo(); }, "Undo", Symbol.Undo, ""));
            menuFlyout.Items.Add(CreateItem(() => { sender.Redo(); }, "Redo", Symbol.Redo, ""));

            menuFlyout.Closed += (_, _) => { sender.Focus(FocusState.Programmatic); };

        }

        private static void CopyWithLineNumbers(CoreTextControlBox sender)
        {
            var sel = sender.CurrentSelectionOrdered;
            if (sel is null) return;

            int startLine = sel.Value.StartLinePos;
            int endLine = sel.Value.EndLinePos;
            int startChar = sel.Value.StartCharacterPos;
            int endChar = sel.Value.EndCharacterPos;

            var sb = new StringBuilder();
            int lineNumWidth = (endLine + 1).ToString().Length;

            for (int i = startLine; i <= endLine; i++)
            {
                string lineText = sender.GetLineText(i) ?? string.Empty;

                // Trim to selection boundaries on first/last lines
                if (i == startLine && i == endLine)
                    lineText = startChar < lineText.Length ? lineText[startChar..Math.Min(endChar, lineText.Length)] : string.Empty;
                else if (i == startLine)
                    lineText = startChar < lineText.Length ? lineText[startChar..] : string.Empty;
                else if (i == endLine)
                    lineText = lineText[..Math.Min(endChar, lineText.Length)];

                sb.AppendLine($"{(i + 1).ToString().PadLeft(lineNumWidth)} | {lineText}");
            }

            var dataPackage = new DataPackage();
            dataPackage.SetText(sb.ToString());
            Clipboard.SetContent(dataPackage);
        }

        public MenuFlyoutItem CreateItem(Action action, string text, Symbol icon, string key)
        {
            var item = new MenuFlyoutItem
            {
                Text = text,
                KeyboardAcceleratorTextOverride = key,
                Icon = new SymbolIcon { Symbol = icon }
            };
            item.Click += delegate
            {
                action();
            };
            return item;
        }
    }
}
