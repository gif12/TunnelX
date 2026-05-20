using System.Collections.Generic;
using AppTunnel.Services;

namespace AppTunnel.Models;

public sealed record CryptoDonationAddress(string Label, string Address);

public static class CryptoDonationAddressList
{
    public static IEnumerable<CryptoDonationAddress> GetAll(LocalizationService localization)
    {
        var t = localization.T;
        return new CryptoDonationAddress[]
        {
            new(t("ترون / USDT روی TRC20"), "TNWV867fQDT6zpLunHgbeMjrN6ic63LQSu"),
            new(t("بیت‌کوین"), "bc1qgx3g47c458fu6smnpqpu0l05hha82rq2xjet4y"),
            new(t("اتریوم / USDT روی ERC20"), "0x72d94Bb250E8802441a0ED05686Ee925BC99Fef5"),
            new("TON", "UQD65oL2Vu2OJDSrwQ0wLLSw3g668SREMJ3VPW9k8b6Sy-Yf"),
            new("BNB Smart Chain", "0xE2a5b01cE2b3713D435Bc16d92eAdd88A82159f0"),
            new("Dogecoin", "DSZRNY65yF679uvjAh6sUAt6YiEEQHwKGb")
        };
    }
}
