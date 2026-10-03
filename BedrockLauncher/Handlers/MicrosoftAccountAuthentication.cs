using System;
using System.Linq;
using System.Threading.Tasks;
using Windows.Security.Authentication.Web.Core;

namespace BedrockLauncher.Handlers
{
    internal sealed class MicrosoftAccountIdentity
    {
        public string Id { get; }
        public string UserName { get; }

        public MicrosoftAccountIdentity(string id, string userName)
        {
            Id = id;
            UserName = userName;
        }
    }

    internal static class MicrosoftAccountAuthentication
    {
        private const string ProviderAuthority = "https://login.microsoft.com";
        private const string ProviderId = "consumers";
        private const string TokenScope =
            "service::dcat.update.microsoft.com::MBI_SSL";
        private const string ClientId =
            "{28520974-CE92-4F36-A219-3F255AF7E61E}";

        internal static async Task<MicrosoftAccountIdentity> SignInAsync()
        {
            var provider =
                await WebAuthenticationCoreManager.FindAccountProviderAsync(
                    ProviderAuthority,
                    ProviderId);

            if (provider == null)
            {
                throw new InvalidOperationException(
                    "The Microsoft consumer account provider is unavailable.");
            }

            var request =
                new WebTokenRequest(
                    provider,
                    TokenScope,
                    ClientId);
            var result =
                await WebAuthenticationCoreManager.RequestTokenAsync(
                    request);

            if (result.ResponseStatus == WebTokenRequestStatus.UserCancel)
                return null;

            if (result.ResponseStatus != WebTokenRequestStatus.Success)
            {
                throw new InvalidOperationException(
                    $"Microsoft sign-in did not complete: {result.ResponseStatus}.");
            }

            var account =
                result.ResponseData
                    .Select(response => response.WebAccount)
                    .FirstOrDefault(candidate => candidate != null);

            if (account == null ||
                string.IsNullOrWhiteSpace(account.Id) ||
                string.IsNullOrWhiteSpace(account.UserName))
            {
                throw new InvalidOperationException(
                    "Microsoft sign-in completed without returning a usable account identity.");
            }

            return new MicrosoftAccountIdentity(
                account.Id,
                account.UserName);
        }
    }
}
