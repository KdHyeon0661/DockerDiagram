using DockerDiagram.Contracts;
using DockerDiagram.Common;
using Docker.DotNet.Models;
using DockerDiagram.Models;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using System.Windows;

namespace DockerDiagram.ViewModels
{
    public sealed class ContainerNetworkDetailViewModel : ViewModelBase
    {
        private string _staticIPv4 = string.Empty;
        private string _staticIPv6 = string.Empty;
        private string _aliasesText = string.Empty;
        private string _driverOptionsText = string.Empty;
        private string _ipv4 = "-";
        private string _ipv6 = "-";
        private bool _isAdvancedExpanded;

        public string NetworkName { get; set; } = string.Empty;
        public string IPv4
        {
            get => _ipv4;
            set => SetProperty(ref _ipv4, value);
        }
        public string IPv6
        {
            get => _ipv6;
            set => SetProperty(ref _ipv6, value);
        }
        public bool IsAdvancedExpanded
        {
            get => _isAdvancedExpanded;
            set => SetProperty(ref _isAdvancedExpanded, value);
        }
        public Action<ContainerNetworkDetailViewModel>? OptionsChanged { get; set; }
        public AsyncRelayCommand? ApplyCommand { get; set; }

        public string StaticIPv4
        {
            get => _staticIPv4;
            set
            {
                if (SetProperty(ref _staticIPv4, value))
                    OptionsChanged?.Invoke(this);
            }
        }

        public string StaticIPv6
        {
            get => _staticIPv6;
            set
            {
                if (SetProperty(ref _staticIPv6, value))
                    OptionsChanged?.Invoke(this);
            }
        }

        public string AliasesText
        {
            get => _aliasesText;
            set
            {
                if (SetProperty(ref _aliasesText, value))
                    OptionsChanged?.Invoke(this);
            }
        }

        public string DriverOptionsText
        {
            get => _driverOptionsText;
            set
            {
                if (SetProperty(ref _driverOptionsText, value))
                    OptionsChanged?.Invoke(this);
            }
        }
    }

    /// <summary>
    /// 컨테이너별 네트워크 연결 옵션과 사전 검증, 적용 작업을 관리합니다.
    /// </summary>
    public sealed class ContainerNetworkViewModel : ViewModelBase
    {
        private readonly NodeViewModel _node;
        private readonly IDialogService _dialogService;
        private Dictionary<string, string> _ipMap = new();
        private Dictionary<string, ContainerNetworkOptions> _optionsMap = new();

        public ContainerNetworkViewModel(NodeViewModel node, IDialogService dialogService)
        {
            _node = node;
            _dialogService = dialogService;
        }

        public ObservableCollection<ContainerNetworkDetailViewModel> Details { get; } = new();

        public string NetworkSummary => FormatSummary(Details.Select(detail => detail.NetworkName), 28);
        public string IPv4Summary => FormatSummary(Details.Select(detail => detail.IPv4), 28);
        public string IPv6Summary => FormatSummary(Details.Select(detail => detail.IPv6), 22);

        public string NetworkDetails => FormatDetails(Details.Select(detail => detail.NetworkName));
        public string IPv4Details => FormatDetails(Details.Select(detail => detail.IPv4));
        public string IPv6Details => FormatDetails(Details.Select(detail => detail.IPv6));

        private static List<string> NormalizeSummaryValues(IEnumerable<string> values)
        {
            return values
                .Where(value => !string.IsNullOrWhiteSpace(value) && value != "-")
                .Select(value => value.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static string FormatSummary(IEnumerable<string> values, int maxLength)
        {
            List<string> items = NormalizeSummaryValues(values);
            if (items.Count == 0) return "-";

            string first = items[0].Length <= maxLength
                ? items[0]
                : $"{items[0][..(maxLength - 1)]}…";
            return items.Count == 1 ? first : $"{first} +{items.Count - 1}";
        }

        private static string FormatDetails(IEnumerable<string> values)
        {
            List<string> items = NormalizeSummaryValues(values);
            return items.Count == 0 ? "-" : string.Join(Environment.NewLine, items);
        }

        public Dictionary<string, string> IpMap
        {
            get => _ipMap;
            set => SetProperty(ref _ipMap, value ?? new Dictionary<string, string>());
        }

        public Dictionary<string, ContainerNetworkOptions> OptionsMap
        {
            get => _optionsMap;
            set => SetProperty(ref _optionsMap, value ?? new Dictionary<string, ContainerNetworkOptions>());
        }

        public ContainerNetworkOptions? GetOptions(string networkName)
        {
            if (OptionsMap.TryGetValue(networkName, out var options))
            {
                var cloned = options.Clone();
                return cloned.HasAnyOption ? cloned : null;
            }

            if (IpMap.TryGetValue(networkName, out var staticIp) && !string.IsNullOrWhiteSpace(staticIp))
                return new ContainerNetworkOptions { StaticIPv4 = staticIp.Trim() };

            var diagramNetworkName = ResolveDiagramNetworkName(networkName);
            if (string.IsNullOrWhiteSpace(diagramNetworkName) ||
                string.Equals(diagramNetworkName, networkName, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            if (OptionsMap.TryGetValue(diagramNetworkName, out var diagramOptions))
            {
                var cloned = diagramOptions.Clone();
                return cloned.HasAnyOption ? cloned : null;
            }

            if (IpMap.TryGetValue(diagramNetworkName, out var diagramStaticIp) &&
                !string.IsNullOrWhiteSpace(diagramStaticIp))
            {
                return new ContainerNetworkOptions { StaticIPv4 = diagramStaticIp.Trim() };
            }

            return null;
        }

        public void UpdateDetails(IEnumerable<KeyValuePair<string, EndpointSettings>> networks)
        {
            var snapshot = networks.ToList();
            var names = new List<string>();
            var addresses = new List<string>();
            var desiredNames = new HashSet<string>(
                snapshot.Select(network => network.Key),
                StringComparer.OrdinalIgnoreCase);

            for (int targetIndex = 0; targetIndex < snapshot.Count; targetIndex++)
            {
                var network = snapshot[targetIndex];
                names.Add(network.Key);
                string ipv4 = network.Value.IPAddress;
                string ipv6 = network.Value.GlobalIPv6Address;
                if (!string.IsNullOrEmpty(ipv4)) addresses.Add(ipv4);

                var detail = Details.FirstOrDefault(existing =>
                    string.Equals(existing.NetworkName, network.Key, StringComparison.OrdinalIgnoreCase));

                if (detail == null)
                {
                    var options = GetOptions(network.Key) ?? new ContainerNetworkOptions();
                    string networkName = network.Key;
                    detail = new ContainerNetworkDetailViewModel
                    {
                        NetworkName = networkName,
                        IPv4 = string.IsNullOrEmpty(ipv4) ? "-" : ipv4,
                        IPv6 = string.IsNullOrEmpty(ipv6) ? "-" : ipv6,
                        StaticIPv4 = options.StaticIPv4,
                        StaticIPv6 = options.StaticIPv6,
                        AliasesText = string.Join(", ", options.Aliases),
                        DriverOptionsText = string.Join(
                            ", ",
                            options.DriverOptions.Select(kv => $"{kv.Key}={kv.Value}")),
                        OptionsChanged = SetOptions,
                        ApplyCommand = new AsyncRelayCommand(_ => ApplyOptionsAsync(networkName))
                    };
                    Details.Insert(targetIndex, detail);
                }
                else
                {
                    int currentIndex = Details.IndexOf(detail);
                    if (currentIndex != targetIndex)
                        Details.Move(currentIndex, targetIndex);

                    detail.IPv4 = string.IsNullOrEmpty(ipv4) ? "-" : ipv4;
                    detail.IPv6 = string.IsNullOrEmpty(ipv6) ? "-" : ipv6;
                }
            }

            for (int index = Details.Count - 1; index >= 0; index--)
            {
                if (!desiredNames.Contains(Details[index].NetworkName))
                    Details.RemoveAt(index);
            }

            _node.ConnectedNetworksString = names.Count > 0 ? string.Join(", ", names) : "None";
            _node.IpAddresses = addresses.Count > 0 ? string.Join(", ", addresses) : "-";
            _node.IPAddress = addresses.Count > 0 ? addresses[0] : "-";

            OnPropertyChanged(nameof(NetworkSummary));
            OnPropertyChanged(nameof(IPv4Summary));
            OnPropertyChanged(nameof(IPv6Summary));
            OnPropertyChanged(nameof(NetworkDetails));
            OnPropertyChanged(nameof(IPv4Details));
            OnPropertyChanged(nameof(IPv6Details));
        }

        public async Task<bool> ValidateBeforeConnectAsync(
            INetworkService networkService,
            string networkName,
            string? dockerNetworkName = null)
        {
            var options = GetOptions(networkName);
            if (options == null || !options.HasAnyOption) return true;

            string inspectName = string.IsNullOrWhiteSpace(dockerNetworkName)
                ? networkName
                : dockerNetworkName;

            NetworkResponse network;
            try
            {
                network = await networkService.InspectNetworkAsync(inspectName);
            }
            catch (Exception ex)
            {
                _dialogService.ShowError(
                    $"'{networkName}' 네트워크 정보를 확인할 수 없어 연결 옵션을 적용할 수 없습니다.\n{ex.Message}",
                    "Network Options");
                return false;
            }

            if (options.DriverOptions.Count > 0 &&
                IsEndpointDriverOptionsDefinitelyUnsupported(network.Driver))
            {
                _dialogService.ShowError(
                    $"'{networkName}' 네트워크의 드라이버({network.Driver})는 endpoint driver options를 지원하지 않습니다.",
                    "Network Options");
                return false;
            }

            if (!string.IsNullOrWhiteSpace(options.StaticIPv4) &&
                !ValidateStaticIp(network, options.StaticIPv4, isIPv6: false, out var ipv4Error))
            {
                _dialogService.ShowError(ipv4Error, "Static IPv4");
                return false;
            }

            if (!string.IsNullOrWhiteSpace(options.StaticIPv6))
            {
                if (network.EnableIPv6 != true)
                {
                    _dialogService.ShowError(
                        $"'{networkName}' 네트워크는 IPv6가 활성화되어 있지 않습니다.\n" +
                        "먼저 네트워크 생성 옵션에서 IPv6를 켜야 Static IPv6를 사용할 수 있습니다.",
                        "Static IPv6");
                    return false;
                }

                if (!ValidateStaticIp(network, options.StaticIPv6, isIPv6: true, out var ipv6Error))
                {
                    _dialogService.ShowError(ipv6Error, "Static IPv6");
                    return false;
                }
            }

            if (IsStaticIpInUseByAnotherContainer(
                network,
                options.StaticIPv4,
                isIPv6: false,
                out var ipv4Owner))
            {
                _dialogService.ShowError(
                    $"Static IPv4 '{options.StaticIPv4}'는 이미 '{ipv4Owner}' 컨테이너가 사용 중입니다.",
                    "IP Conflict");
                return false;
            }

            if (IsStaticIpInUseByAnotherContainer(
                network,
                options.StaticIPv6,
                isIPv6: true,
                out var ipv6Owner))
            {
                _dialogService.ShowError(
                    $"Static IPv6 '{options.StaticIPv6}'는 이미 '{ipv6Owner}' 컨테이너가 사용 중입니다.",
                    "IP Conflict");
                return false;
            }

            return true;
        }

        private void SetOptions(ContainerNetworkDetailViewModel detail)
        {
            if (string.IsNullOrWhiteSpace(detail.NetworkName)) return;

            string optionKey = ResolveDiagramNetworkName(detail.NetworkName) ?? detail.NetworkName;
            var options = new ContainerNetworkOptions
            {
                StaticIPv4 = detail.StaticIPv4.Trim(),
                StaticIPv6 = detail.StaticIPv6.Trim(),
                Aliases = ParseList(detail.AliasesText),
                DriverOptions = ParseKeyValueText(detail.DriverOptionsText)
            };

            if (options.HasAnyOption)
                OptionsMap[optionKey] = options;
            else
                OptionsMap.Remove(optionKey);

            if (!string.Equals(optionKey, detail.NetworkName, StringComparison.OrdinalIgnoreCase))
            {
                OptionsMap.Remove(detail.NetworkName);
                IpMap.Remove(detail.NetworkName);
            }

            if (string.IsNullOrWhiteSpace(options.StaticIPv4))
                IpMap.Remove(optionKey);
            else
                IpMap[optionKey] = options.StaticIPv4;

            OnPropertyChanged(nameof(IpMap));
            OnPropertyChanged(nameof(OptionsMap));
            _node.NotifyModified();
        }

        private string? ResolveDiagramNetworkName(string dockerNetworkName)
        {
            return _node.ParentSheet?.Groups
                .Where(group => group.Type == GroupType.Network && group.ContainedNodes.Contains(_node))
                .FirstOrDefault(group =>
                    string.Equals(group.Title, dockerNetworkName, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(group.DockerNetworkName, dockerNetworkName, StringComparison.OrdinalIgnoreCase))
                ?.Title;
        }

        private async Task ApplyOptionsAsync(string networkName)
        {
            if (_node.ParentSheet?.DockerService is not INetworkService networkService ||
                string.IsNullOrWhiteSpace(_node.ContainerId))
            {
                return;
            }

            try
            {
                if (!ValidateDriverOptionsText(networkName)) return;
                if (!await ValidateBeforeConnectAsync(networkService, networkName)) return;

                var result = _dialogService.ShowYesNoCancel(
                    $"'{networkName}' 네트워크 옵션을 적용하려면 컨테이너 네트워크 연결을 재생성해야 합니다.\n" +
                    "작업 중 아주 짧게 해당 네트워크 연결이 끊길 수 있습니다.\n\n계속하시겠습니까?",
                    "Apply Network Options");
                if (result != DialogChoice.Yes) return;

                try
                {
                    await networkService.DisconnectNetworkAsync(networkName, _node.ContainerId);
                }
                catch (Exception ex) when (
                    ex.Message.Contains("is not connected") ||
                    ex.Message.Contains("연결되어 있지"))
                {
                    Debug.WriteLine($"[DockerDiscovery] Apply network options skipped disconnect: {ex.Message}");
                }

                try
                {
                    await networkService.ConnectNetworkAsync(
                        networkName,
                        _node.ContainerId,
                        GetOptions(networkName));
                }
                catch
                {
                    try
                    {
                        await networkService.ConnectNetworkAsync(networkName, _node.ContainerId);
                    }
                    catch (Exception rollbackException)
                    {
                        Debug.WriteLine(
                            $"[DockerDiscovery] Network option rollback failed: {rollbackException.Message}");
                    }

                    throw;
                }

                await _node.RefreshDetailsAsync();
            }
            catch (Exception ex)
            {
                _dialogService.ShowError(
                    $"'{_node.Name}' 컨테이너의 '{networkName}' 네트워크 옵션 적용 중 오류가 발생했습니다.\n{ex.Message}",
                    "Network Options Error");
            }
        }

        private bool ValidateDriverOptionsText(string networkName)
        {
            var detail = Details.FirstOrDefault(item => item.NetworkName == networkName);
            if (detail == null || string.IsNullOrWhiteSpace(detail.DriverOptionsText)) return true;

            var invalid = detail.DriverOptionsText
                .Split(new[] { ',', ';', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(item => item.Trim())
                .FirstOrDefault(item => !item.Contains('=') || item.StartsWith("="));
            if (invalid == null) return true;

            _dialogService.ShowError(
                $"Endpoint driver options는 key=value 형식으로 입력해야 합니다.\n\n문제 항목: {invalid}",
                "Network Options");
            return false;
        }

        private bool IsStaticIpInUseByAnotherContainer(
            NetworkResponse network,
            string ip,
            bool isIPv6,
            out string owner)
        {
            owner = string.Empty;
            if (string.IsNullOrWhiteSpace(ip) || network.Containers == null) return false;

            foreach (var container in network.Containers)
            {
                if (IsSameContainer(container.Key)) continue;

                string endpointIp = NormalizeEndpointIp(
                    isIPv6 ? container.Value.IPv6Address : container.Value.IPv4Address);
                if (string.IsNullOrWhiteSpace(endpointIp) ||
                    !string.Equals(endpointIp, ip, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                owner = string.IsNullOrWhiteSpace(container.Value.Name)
                    ? container.Key
                    : container.Value.Name;
                return true;
            }

            return false;
        }

        private bool IsSameContainer(string id)
        {
            return !string.IsNullOrWhiteSpace(id) &&
                   !string.IsNullOrWhiteSpace(_node.ContainerId) &&
                   (id.StartsWith(_node.ContainerId, StringComparison.OrdinalIgnoreCase) ||
                    _node.ContainerId.StartsWith(id, StringComparison.OrdinalIgnoreCase));
        }

        private static bool ValidateStaticIp(
            NetworkResponse network,
            string ip,
            bool isIPv6,
            out string error)
        {
            error = string.Empty;
            string familyName = isIPv6 ? "IPv6" : "IPv4";

            if (!IPAddress.TryParse(ip, out var parsed) || IsIPv6Address(parsed) != isIPv6)
            {
                error = $"'{ip}'는 올바른 {familyName} 주소가 아닙니다.";
                return false;
            }

            var subnets = network.IPAM?.Config?
                .Select(config => config.Subnet)
                .Where(subnet => !string.IsNullOrWhiteSpace(subnet) && IsCidrFamily(subnet!, isIPv6))
                .ToList() ?? new List<string>();

            if (subnets.Count == 0)
            {
                error =
                    $"'{network.Name}' 네트워크에서 {familyName} subnet 정보를 찾을 수 없어 Static {familyName}를 안전하게 검증할 수 없습니다.";
                return false;
            }

            if (!subnets.Any(subnet => IsIpInCidr(ip, subnet!)))
            {
                error =
                    $"'{ip}'는 '{network.Name}' 네트워크의 {familyName} subnet 범위 안에 없습니다.\n" +
                    $"Subnet: {string.Join(", ", subnets)}";
                return false;
            }

            return true;
        }

        private static bool IsEndpointDriverOptionsDefinitelyUnsupported(string? driver)
        {
            return string.Equals(driver, "host", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(driver, "none", StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizeEndpointIp(string? endpointIp)
        {
            return string.IsNullOrWhiteSpace(endpointIp)
                ? string.Empty
                : endpointIp.Split('/')[0].Trim();
        }

        private static bool IsCidrFamily(string cidr, bool isIPv6)
        {
            string ipPart = cidr.Split('/')[0];
            return IPAddress.TryParse(ipPart, out var ip) && IsIPv6Address(ip) == isIPv6;
        }

        private static bool IsIpInCidr(string ip, string cidr)
        {
            var parts = cidr.Split('/');
            if (parts.Length != 2 ||
                !IPAddress.TryParse(ip, out var address) ||
                !IPAddress.TryParse(parts[0], out var network) ||
                !int.TryParse(parts[1], out var prefixLength))
            {
                return false;
            }

            var addressBytes = address.GetAddressBytes();
            var networkBytes = network.GetAddressBytes();
            if (addressBytes.Length != networkBytes.Length ||
                prefixLength < 0 ||
                prefixLength > addressBytes.Length * 8)
            {
                return false;
            }

            int fullBytes = prefixLength / 8;
            int remainingBits = prefixLength % 8;
            for (int i = 0; i < fullBytes; i++)
            {
                if (addressBytes[i] != networkBytes[i]) return false;
            }

            if (remainingBits == 0) return true;

            var mask = (byte)(0xFF << (8 - remainingBits));
            return (addressBytes[fullBytes] & mask) == (networkBytes[fullBytes] & mask);
        }

        private static bool IsIPv6Address(IPAddress ip) => ip.GetAddressBytes().Length == 16;

        private static List<string> ParseList(string text)
        {
            return (text ?? string.Empty)
                .Split(new[] { ',', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(item => item.Trim())
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static Dictionary<string, string> ParseKeyValueText(string text)
        {
            return (text ?? string.Empty)
                .Split(new[] { ',', ';', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(item => item.Trim())
                .Where(item => item.Contains('=') && !item.StartsWith("="))
                .Select(item => item.Split(new[] { '=' }, 2))
                .GroupBy(parts => parts[0].Trim(), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => group.Last()[1].Trim(),
                    StringComparer.OrdinalIgnoreCase);
        }
    }
}
