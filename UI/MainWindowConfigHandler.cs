﻿using SPCode.UI.Components;
using SPCode.UI.Windows;
using System.Windows;
using System.Windows.Controls;

namespace SPCode.UI
{
    public partial class MainWindow
    {
        public void FillConfigMenu()
        {
            ConfigMenu.Items.Clear();
            for (int i = 0; i < Program.Configs.Length; ++i)
            {
                MenuItem item = new MenuItem
                {
                    Header = Program.Configs[i].Name,
                    IsCheckable = true,
                    IsChecked = i == Program.SelectedConfig
                };
                item.Click += item_Click;
                ConfigMenu.Items.Add(item);
            }
            ConfigMenu.Items.Add(new Separator());
            MenuItem editItem = new MenuItem() { Header = Program.Translations.GetLanguage("EditConfig") };
            editItem.Click += editItem_Click;
            ConfigMenu.Items.Add(editItem);
        }

        private void editItem_Click(object sender, RoutedEventArgs e)
        {
            ConfigWindow configWindow = new ConfigWindow() { Owner = this, ShowInTaskbar = false };
            configWindow.ShowDialog();
        }

        private void item_Click(object sender, RoutedEventArgs e)
        {
            string name = (string)((MenuItem)sender).Header;
            ChangeConfig(name);
        }

        public void ChangeConfig(int index)
        {
            if (index < 0 || index >= Program.Configs.Length)
            {
                return;
            }
            Program.Configs[index].LoadSMDef();
            string name = Program.Configs[index].Name;
            for (int i = 0; i < ConfigMenu.Items.Count - 2; ++i)
            {
                ((MenuItem)ConfigMenu.Items[i]).IsChecked = name == (string)((MenuItem)ConfigMenu.Items[i]).Header;
            }
            Program.SelectedConfig = index;
            Program.OptionsObject.Program_SelectedConfig = Program.Configs[Program.SelectedConfig].Name;
            EditorElement[] editors = GetAllEditorElements();
            if (editors != null)
            {
                foreach (var editor in editors)
                {
                    editor.LoadAutoCompletes();
                    editor.editor.SyntaxHighlighting = new AeonEditorHighlighting();
                    editor.InvalidateVisual();
                }
            }
        }

        private void ChangeConfig(string name)
        {
            for (int i = 0; i < Program.Configs.Length; ++i)
            {
                if (Program.Configs[i].Name == name)
                {
                    ChangeConfig(i);
                    return;
                }
            }
        }

    }
}
