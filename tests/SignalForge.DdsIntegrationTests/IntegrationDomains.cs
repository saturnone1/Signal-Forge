internal static class IntegrationDomains
{
    // Windows의 Hyper-V 예약 UDP 포트를 피한다. 운영 앱의 도메인은 변경하지 않는다.
    public static int Resolve(int original)
    {
        var text = Environment.GetEnvironmentVariable("DDS_INTEGRATION_DOMAIN_BASE");
        var basis = text is null ? (OperatingSystem.IsWindows() ? 141 : 191) : int.Parse(text);
        if (basis < 5 || basis > 228) throw new ArgumentOutOfRangeException(nameof(basis));
        return basis + original - 191;
    }
}
