using System.Globalization;
using System.Net;
using System.Xml.Linq;
using ASAP.Models.Dds;

namespace ASAP.Dds;

/// <summary>Translates UI transport settings to per-session QoS XML consumed by DDSClient.</summary>
public static class DdsSessionQos
{
    public sealed record Effective(string Xml, string ParticipantProfile);

    public static Effective Create(string source, DdsTransportSettings settings, string? qosLibraryName = null)
    {
        if (settings.DomainId is < 0 or > 232) throw new InvalidOperationException("DDS domain must be between 0 and 232.");
        if (!Enum.IsDefined(settings.DiscoveryMode)) throw new InvalidOperationException("Unknown DDS discovery mode.");
        if (settings.SendBufferSize is <= 0 || settings.ReceiveBufferSize is <= 0)
            throw new InvalidOperationException("DDS socket buffer sizes must be positive.");
        if (settings.DiscoveryMode == DdsDiscoveryMode.Multicast && !string.IsNullOrWhiteSpace(settings.MulticastAddress))
        {
            if (!IPAddress.TryParse(settings.MulticastAddress, out var ip) ||
                ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork || ip.GetAddressBytes()[0] is < 224 or > 239)
                throw new InvalidOperationException("Discovery multicast address must be an IPv4 multicast address.");
        }
        var document = XDocument.Parse(source);
        if (document.Descendants("include").Any() || document.DescendantNodes().OfType<XProcessingInstruction>().Any())
            throw new InvalidOperationException("Session QoS must be self-contained; external XML includes/directives are not supported.");
        foreach (var candidate in document.Descendants("qos_profile"))
            _ = ParticipantPolicy(candidate); // reject ambiguous aliases even in a currently unused profile
        var library = DdsConfigParser.SelectLibrary(document, qosLibraryName);
        var libraryName = (string?)library.Attribute("name") ?? throw new InvalidOperationException("QoS 라이브러리 이름이 필요합니다.");
        var profiles = library.Elements("qos_profile").ToArray();
        var selectedName = settings.ParticipantQosProfile;
        if (selectedName?.Contains("::", StringComparison.Ordinal) == true)
        {
            if (!selectedName.StartsWith(libraryName + "::", StringComparison.Ordinal))
                throw new InvalidOperationException("Participant와 토픽은 선택한 망의 QoS 라이브러리에 속해야 합니다.");
            selectedName = selectedName[(libraryName.Length + 2)..];
        }
        var defaultProfile = string.IsNullOrWhiteSpace(selectedName)
            ? profiles.FirstOrDefault(p => (string?)p.Attribute("is_default_qos") == "true") ?? profiles.FirstOrDefault()
            : profiles.SingleOrDefault(p => (string?)p.Attribute("name") == selectedName);
        if (defaultProfile is null) throw new InvalidOperationException("사용할 Participant QoS 프로파일이 없습니다.");
        var name = "SignalForgeSession_" + Guid.NewGuid().ToString("N");
        var profile = new XElement("qos_profile", new XAttribute("name", name),
            new XAttribute("base_name", libraryName + "::" + (string?)defaultProfile.Attribute("name")));
        var participant = new XElement("participant_qos");
        profile.Add(participant);
        var peers = ResolvePolicy(defaultProfile, "discovery", "initial_peers") ?? new XElement("initial_peers");
        if (settings.DiscoveryPeers.Count > 0)
            peers = new XElement("initial_peers", settings.DiscoveryPeers.Select(peer => new XElement("element", peer)));
        if (settings.DiscoveryMode == DdsDiscoveryMode.Unicast)
            peers.Elements("element").Where(element => IsMulticastPeer(element.Value)).Remove();
        var addresses = new XElement("multicast_receive_addresses");
        if (settings.DiscoveryMode == DdsDiscoveryMode.Multicast)
        {
            addresses = ResolvePolicy(defaultProfile, "discovery", "multicast_receive_addresses") ?? addresses;
            var address = string.IsNullOrWhiteSpace(settings.MulticastAddress) ? "239.255.0.1" : settings.MulticastAddress;
            if (!addresses.Elements("element").Any(e => e.Value == address)) addresses.Add(new XElement("element", address));
            if (!peers.Elements("element").Any(e => e.Value == "udpv4://" + address)) peers.Add(new XElement("element", "udpv4://" + address));
        }
        if (settings.DiscoveryMode != DdsDiscoveryMode.FromQos || settings.DiscoveryPeers.Count > 0)
        {
            var discovery = new XElement("discovery", peers);
            if (settings.DiscoveryMode != DdsDiscoveryMode.FromQos) discovery.Add(addresses);
            participant.Add(discovery);
        }
        var properties = ResolvePolicy(defaultProfile, "property", "value") ?? new XElement("value");
        void Set(string key, string value)
        {
            properties.Elements("element").Where(e => (string?)e.Element("name") == key).Remove();
            properties.Add(new XElement("element", new XElement("name", key), new XElement("value", value)));
        }
        if (settings.AllowInterfaces.Count > 0) Set("dds.transport.UDPv4.builtin.parent.allow_interfaces", string.Join(",", settings.AllowInterfaces));
        if (settings.DenyInterfaces.Count > 0) Set("dds.transport.UDPv4.builtin.parent.deny_interfaces", string.Join(",", settings.DenyInterfaces));
        if (settings.SendBufferSize is int send) Set("dds.transport.UDPv4.builtin.send_socket_buffer_size", send.ToString(CultureInfo.InvariantCulture));
        if (settings.ReceiveBufferSize is int receive) Set("dds.transport.UDPv4.builtin.recv_socket_buffer_size", receive.ToString(CultureInfo.InvariantCulture));
        if (properties.HasElements) participant.Add(new XElement("property", properties));
        library.Add(profile);
        return new Effective(document.ToString(), libraryName + "::" + name);
    }

    private static bool IsMulticastPeer(string peer)
    {
        var address = peer[(peer.LastIndexOf('/') + 1)..].Split(':')[0];
        return IPAddress.TryParse(address, out var ip) && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
            && ip.GetAddressBytes()[0] is >= 224 and <= 239;
    }

    private static XElement? ResolvePolicy(XElement profile, string policy, string member, HashSet<XElement>? visited = null)
    {
        visited ??= [];
        if (!visited.Add(profile)) throw new InvalidOperationException("QoS profile inheritance contains a cycle.");
        var value = ParticipantPolicy(profile)?.Element(policy)?.Element(member);
        if (value is not null) return new XElement(value);
        var parentName = (string?)profile.Attribute("base_name");
        if (parentName is null) return null;
        var parts = parentName.Split("::");
        var library = parts.Length == 2 ? parts[0] : (string?)profile.Parent?.Attribute("name");
        var parent = profile.Document!.Descendants("qos_library").Where(l => (string?)l.Attribute("name") == library)
            .Elements("qos_profile").SingleOrDefault(p => (string?)p.Attribute("name") == parts[^1]);
        return parent is null ? null : ResolvePolicy(parent, policy, member, visited);
    }

    private static XElement? ParticipantPolicy(XElement profile)
    {
        var blocks = profile.Elements().Where(e => e.Name == "participant_qos" || e.Name == "domain_participant_qos").ToArray();
        if (blocks.Length > 1)
            throw new InvalidOperationException($"QoS profile '{(string?)profile.Attribute("name")}' has conflicting participant_qos/domain_participant_qos blocks.");
        return blocks.SingleOrDefault();
    }
}
