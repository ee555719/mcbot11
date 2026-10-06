using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using McBot11.Auth;

namespace McBot11.App
{
    /// <summary>
    /// 角色选择弹窗（单选 RadioButton 列出 availableProfiles，底部确定/取消）。
    /// 返回用户选中的角色；取消返回 null。
    /// </summary>
    public partial class RoleSelectDialog : Window
    {
        private readonly List<AuthProfile> _roles;

        /// <summary>弹出角色选择；返回选中角色，取消返回 null。</summary>
        public static AuthProfile? Show(Window? owner, string hint, IReadOnlyList<AuthProfile> roles, string? preselectId)
        {
            if (roles == null || roles.Count == 0) return null;
            var dlg = new RoleSelectDialog(hint, roles, preselectId) { Owner = owner };
            return dlg.ShowDialog() == true ? dlg._picked : null;
        }

        private AuthProfile? _picked;

        private RoleSelectDialog(string hint, IReadOnlyList<AuthProfile> roles, string? preselectId)
        {
            InitializeComponent();
            _roles = new List<AuthProfile>(roles);
            HintText.Text = hint;

            AuthProfile? preselect = null;
            foreach (var role in _roles)
            {
                string shortId = (role.Id ?? "").Length > 8 ? role.Id.Substring(0, 8) + "…" : role.Id;
                var rb = new RadioButton
                {
                    Content = $"{role.Name}    ({shortId})",
                    Tag = role,
                    GroupName = "roles"
                };
                if (preselectId != null &&
                    string.Equals((role.Id ?? "").Replace("-", ""), preselectId.Replace("-", ""), StringComparison.OrdinalIgnoreCase))
                    preselect = role;
                RolePanel.Children.Add(rb);
            }
            preselect ??= _roles[0];
            foreach (var child in RolePanel.Children)
                if (child is RadioButton rb2 && ReferenceEquals(rb2.Tag, preselect))
                    rb2.IsChecked = true;
        }

        private void OnOk(object sender, RoutedEventArgs e)
        {
            foreach (var child in RolePanel.Children)
            {
                if (child is RadioButton rb && rb.IsChecked == true && rb.Tag is AuthProfile role)
                {
                    _picked = role;
                    DialogResult = true;
                    return;
                }
            }
            ErrorText.Text = "请先选择一个角色";
            ErrorText.Visibility = Visibility.Visible;
        }

        private void OnCancel(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }
    }
}
