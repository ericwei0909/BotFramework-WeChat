using System.Threading.Tasks;
using Microsoft.Bot.Builder.Adapters.WeChat.Schema;

namespace Microsoft.Bot.Builder.Adapters.WeChat.Common
{
    public interface IAccessTokenProvider
    {
        Task<WeChatAccessToken> GetAccessToken(string appId, string appSecret);
    }
}
