using System.Threading.Tasks;

namespace Microsoft.Bot.Builder.Adapters.WeChat.Common
{
    public interface IWeChatAccessTokenProvider
    {
        Task<string> GetAccessToken(string appId, string appSecret);
    }
}
