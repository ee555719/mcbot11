using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using McBot11.Auth;

namespace McBot11.App
{
    /// <summary>第三方账号输入（添加/编辑对话框的输入与结果）。</summary>
    public sealed class AccountInput
    {
        public string ServerRoot { get; set; } = "https://littleskin.cn/api/yggdrasil";
        public string Email { get; set; } = "";
        /// <summary>密码（仅内存，对话框与验证过程使用，不直接落盘）。</summary>
        public string Password { get; set; } = "";
        public string DisplayName { get; set; } = "";
        /// <summary>是否保存密码（DPAPI 加密落盘）。</summary>
        public bool SavePassword { get; set; } = true;
        /// <summary>编辑模式（密码留空表示保持原密码）。</summary>
        public bool IsEdit { get; set; }
        /// <summary>强制要求输入密码（token 失效且未保存密码时的补录场景）。</summary>
        public bool RequirePassword { get; set; }
        /// <summary>已存在的账号 ID（编辑时使用；新增时为空）。</summary>
        public string? AccountId { get; set; }
        /// <summary>用户在角色列表中选定的角色 UUID。</summary>
        public string? SelectedProfileId { get; set; }
        /// <summary>用户选定的角色名。</summary>
        public string? SelectedProfileName { get; set; }
    }

    /// <summary>
    /// 第三方账号添加/编辑对话框：收集根地址、邮箱、密码、展示名；
    /// 「获取角色列表」拉取 availableProfiles 供预览与选择（选中写入 SelectedProfileId）；
    /// 点击「验证并保存」时先执行注入的验证回调（Yggdrasil authenticate），
    /// 通过才返回结果，失败在对话框内给出可读错误提示。
    /// </summary>
    public partial class AccountDialog : Window
    {
        private readonly AccountInput _input;
        private readonly bool _isEdit;
        private readonly Func<AccountInput, Task<string?>>? _verify;
        private readonly Func<AccountInput, Task<List<AuthProfile>?>>? _fetchRoles;
        private bool _busy;
        private bool _syncingRole;
        private string _pwd = "";

        /// <summary>验证回调：返回 null 表示通过，否则返回可展示的错误信息。</summary>
        public static AccountInput? Show(
            Window? owner,
            AccountInput input,
            Func<AccountInput, Task<string?>>? verify,
            Func<AccountInput, Task<List<AuthProfile>?>>? fetchRoles = null)
        {
            var dlg = new AccountDialog(input, verify, fetchRoles) { Owner = owner };
            return dlg.ShowDialog() == true ? dlg._input : null;
        }

        private AccountDialog(
            AccountInput input,
            Func<AccountInput, Task<string?>>? verify,
            Func<AccountInput, Task<List<AuthProfile>?>>? fetchRoles)
        {
            InitializeComponent();
            _input = input;
            _isEdit = input.IsEdit;
            _verify = verify;
            _fetchRoles = fetchRoles;
            _pwd = input.Password ?? "";
            Title = _isEdit ? "编辑第三方账号" : "添加第三方账号";
            DataContext = input;
            PwdBox.Password = _pwd;
            SavePasswordBox.IsChecked = input.SavePassword;
            UpdateSelectedRoleText();
            if (input.RequirePassword)
                ErrorText.Text = "该账号未保存密码，登录前必须输入密码并验证。";
            else if (_isEdit && string.IsNullOrEmpty(_pwd))
                ErrorText.Text = "密码留空 = 保持原密码；填写则按新密码验证。";
        }

        private void OnPwdChanged(object sender, RoutedEventArgs e) => _pwd = PwdBox.Password;

        /// <summary>把界面字段同步进 input（验证 / 获取角色前调用）。</summary>
        private AccountInput Collect()
        {
            _input.ServerRoot = (_input.ServerRoot ?? "").Trim();
            _input.Email = (_input.Email ?? "").Trim();
            _input.DisplayName = (_input.DisplayName ?? "").Trim();
            _input.Password = _pwd ?? "";
            _input.SavePassword = SavePasswordBox.IsChecked == true;
            // 新增账号先分配 ID，使「获取角色」「验证」与最终保存共用同一 token 缓存键
            if (!_isEdit && string.IsNullOrEmpty(_input.AccountId))
                _input.AccountId = Guid.NewGuid().ToString();
            return _input;
        }

        private void UpdateSelectedRoleText()
        {
            SelectedRoleText.Text = string.IsNullOrEmpty(_input.SelectedProfileId)
                ? "（未选择角色）"
                : $"已选角色：{_input.SelectedProfileName}  ({_input.SelectedProfileId})";
        }

        private async void OnFetchRoles(object sender, RoutedEventArgs e)
        {
            if (_busy) return;
            if (_fetchRoles == null)
            {
                ShowError("当前操作不支持获取角色列表");
                return;
            }
            var input = Collect();
            _busy = true;
            FetchRolesButton.IsEnabled = false;
            try
            {
                ShowError("");
                var roles = await _fetchRoles(input);
                RoleList.ItemsSource = roles;
                if (roles == null || roles.Count == 0)
                {
                    ShowError("该账号没有可用角色，请先到皮肤站创建角色");
                    return;
                }
                if (roles.Count == 1)
                {
                    RoleList.SelectedIndex = 0;
                }
                else
                {
                    int idx = -1;
                    for (int i = 0; i < roles.Count; i++)
                    {
                        if (string.Equals((roles[i].Id ?? "").Replace("-", ""),
                                (input.SelectedProfileId ?? "").Replace("-", ""),
                                StringComparison.OrdinalIgnoreCase))
                        {
                            idx = i;
                            break;
                        }
                    }
                    if (idx >= 0) RoleList.SelectedIndex = idx;
                }
                if (roles.Count > 1 && RoleList.SelectedIndex < 0)
                    ShowError("该账号有多个角色，请在列表中选择登录角色");
            }
            catch (Exception ex)
            {
                ShowError(ex.Message);
            }
            finally
            {
                _busy = false;
                FetchRolesButton.IsEnabled = true;
            }
        }

        private void OnRoleSelected(object sender, SelectionChangedEventArgs e)
        {
            if (_syncingRole) return;
            if (RoleList.SelectedItem is AuthProfile role)
            {
                _syncingRole = true;
                _input.SelectedProfileId = role.Id;
                _input.SelectedProfileName = role.Name;
                _syncingRole = false;
                UpdateSelectedRoleText();
                if (ErrorText.Text.StartsWith("该账号有多个角色", StringComparison.Ordinal))
                    ShowError("");
            }
        }

        private async void OnOk(object sender, RoutedEventArgs e)
        {
            if (_busy) return;

            var input = Collect();

            if (input.ServerRoot.Length == 0) { ShowError("认证服务器根地址不能为空"); return; }
            if (input.Email.Length == 0) { ShowError("邮箱 / 用户名不能为空"); return; }
            if (input.RequirePassword && input.Password.Length == 0) { ShowError("请输入密码"); return; }
            if (!_isEdit && input.Password.Length == 0) { ShowError("密码不能为空"); return; }
            if (_isEdit && input.Password.Length == 0 && input.SavePassword && !input.RequirePassword)
            {
                // 保持原密码，无需重新验证
                DialogResult = true;
                return;
            }

            if (_verify != null)
            {
                _busy = true;
                OkButton.IsEnabled = false;
                OkButton.Content = "验证中…";
                ShowError("");
                try
                {
                    string? err = await _verify(input).ConfigureAwait(true);
                    if (err != null)
                    {
                        ShowError(err);
                        return;
                    }
                }
                catch (Exception ex)
                {
                    ShowError(ex.Message);
                    return;
                }
                finally
                {
                    _busy = false;
                    OkButton.IsEnabled = true;
                    OkButton.Content = "验证并保存";
                }
            }
            DialogResult = true;
        }

        private void OnCancel(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }

        private void ShowError(string message)
        {
            if (string.IsNullOrEmpty(message))
            {
                ErrorText.Visibility = Visibility.Collapsed;
                ErrorText.Text = "";
            }
            else
            {
                ErrorText.Text = message;
                ErrorText.Visibility = Visibility.Visible;
            }
        }
    }
}
