﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;
using Microsoft.AspNetCore.Http;
using Microsoft.Bot.Builder.Adapters.WeChat.Helpers;
using Microsoft.Bot.Builder.Adapters.WeChat.Schema;
using Microsoft.Bot.Builder.Adapters.WeChat.Schema.Requests;
using Microsoft.Bot.Builder.Adapters.WeChat.Schema.Responses;
using Microsoft.Bot.Schema;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Microsoft.Bot.Builder.Adapters.WeChat
{
    /// <summary>
    /// Represents a adapter that can connect a bot to WeChat endpoint.
    /// </summary>
    public class WeChatHttpAdapter : BotAdapter, IDisposable
    {
        /// <summary>
        /// Key to get all response from bot in a single turn.
        /// </summary>
        private const string TurnResponseKey = "turnResponse";

        private readonly WeChatMessageMapper _wechatMessageMapper;
        private readonly WeChatClient _wechatClient;
        private readonly ILogger _logger;
        private readonly IBackgroundTaskQueue _taskQueue;

        public WeChatHttpAdapter(
                    WeChatClient client,
                    WeChatMessageMapper messageMapper,
                    IBackgroundTaskQueue taskQueue = null,
                    ILogger logger = null)
        {
            _wechatClient = client;
            _wechatMessageMapper = messageMapper;
            _logger = logger ?? NullLogger.Instance;
            _taskQueue = taskQueue;
        }

        /// <summary>
        /// Get access token depends on the current settings.
        /// </summary>
        /// <param name="settings">WeChat settings.</param>
        /// <param name="forceRefresh">If force refresh the token.</param>
        /// <returns>The access token string.</returns>
        public async Task<string> GetWeChatAccessToken(WeChatSettings settings, bool forceRefresh)
        {
            var accessToken = await _wechatClient.GetAccessTokenAsync(settings, forceRefresh).ConfigureAwait(false);
            return accessToken;
        }

        /// <summary>
        /// Standard BotBuilder adapter method to delete a previous message.
        /// </summary>
        /// <param name="turnContext">A TurnContext representing the current incoming message and environment.</param>
        /// <param name="reference">An object in the form "{activityId: `id of message to delete`, conversation: { id: `id of channel`}}".</param>
        /// <param name="cancellationToken">A cancellation token for the task.</param>
        /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
        public override Task DeleteActivityAsync(ITurnContext turnContext, ConversationReference reference, CancellationToken cancellationToken)
        {
            return Task.FromException<ResourceResponse>(new NotSupportedException("WeChat does not support deleting activities."));
        }

        /// <summary>
        /// Standard BotBuilder adapter method to update a previous message with new content.
        /// </summary>
        /// <param name="turnContext">A TurnContext representing the current incoming message and environment.</param>
        /// <param name="activity">The updated activity in the form '{id: `id of activity to update`, ...}'.</param>
        /// <param name="cancellationToken">A cancellation token for the task.</param>
        /// <returns>A resource response with the Id of the updated activity.</returns>
        public override Task<ResourceResponse> UpdateActivityAsync(ITurnContext turnContext, Activity activity, CancellationToken cancellationToken)
        {
            return Task.FromException<ResourceResponse>(new NotSupportedException("WeChat does not support updating activities."));
        }

        /// <summary>
        /// Standard BotBuilder adapter method to send a message from the bot to the messaging API.
        /// </summary>
        /// <param name="turnContext">A TurnContext representing the current incoming message and environment.</param>
        /// <param name="activities">An array of outgoing activities to be sent back to the messaging API.</param>
        /// <param name="cancellationToken">A cancellation token for the task.</param>
        /// <returns>A resource response array with the message Sids.</returns>
        public override async Task<ResourceResponse[]> SendActivitiesAsync(ITurnContext turnContext, Activity[] activities, CancellationToken cancellationToken)
        {
            if (turnContext == null)
            {
                throw new ArgumentNullException(nameof(turnContext));
            }

            if (activities == null)
            {
                throw new ArgumentNullException(nameof(activities));
            }

            if (activities.Length == 0)
            {
                throw new ArgumentException("Expecting one or more activities, but the array was empty.", nameof(activities));
            }

            var responses = new ResourceResponse[activities.Length];

            /*
             * NOTE: we're using for here (vs. foreach) because we want to simultaneously index into the
             * activities array to get the activity to process as well as use that index to assign
             * the response to the responses array and this is the most cost effective way to do that.
             */
            for (var index = 0; index < activities.Length; index++)
            {
                var activity = activities[index];
                var response = default(ResourceResponse);

                _logger.LogInformation($"Sending activity. ReplyToId: {activity.ReplyToId}");

                if (activity.Type == ActivityTypesEx.Delay)
                {
                    // The Activity Schema doesn't have a delay type build in, so it's simulated
                    // here in the Bot. This matches the behavior in the Node connector.
                    var delayMs = (int)activity.Value;
                    await Task.Delay(delayMs, cancellationToken).ConfigureAwait(false);

                    // No need to create a response. One will be created below.
                }
                else if (activity.Type == ActivityTypes.Message || activity.Type == ActivityTypes.EndOfConversation)
                {
                    var currentResponses = turnContext.TurnState.Get<List<Activity>>(TurnResponseKey);
                    currentResponses.Add(activity);
                }
                else
                {
                    _logger.LogInformation($"WeChatAdapter.SendActivities(): Activities of type '{activity.Type}' aren't supported.");
                }

                // If No response is set, then default to a "simple" response. This can't really be done
                // above, as there are cases where the ReplyTo/SendTo methods will also return null
                // (See below) so the check has to happen here.
                if (response == null)
                {
                    response = new ResourceResponse(activity.Id ?? string.Empty);
                }

                responses[index] = response;
            }

            return responses;
        }

        /// <summary>
        /// Sends a message to a conversation from OC service.
        /// </summary>
        /// <param name="settings">WeChat settings.</param>
        /// <param name="botAppId">The application ID of the bot. This parameter is ignored in
        /// single tenant the Adapters (Console, Test, etc) but is critical to the BotFrameworkAdapter
        /// which is multi-tenant aware. </param>
        /// <param name="reference">A reference to the conversation to continue.</param>
        /// <param name="callback">The method to call for the resulting bot turn.</param>
        /// <param name="cancellationToken">A cancellation token that can be used by other objects.</param>
        /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
        public async Task<object> ContinueConversation(WeChatSettings settings, string botAppId, ConversationReference reference, BotCallbackHandler callback, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(botAppId))
            {
                throw new ArgumentNullException(nameof(botAppId));
            }

            if (reference == null)
            {
                throw new ArgumentNullException(nameof(reference));
            }

            if (callback == null)
            {
                throw new ArgumentNullException(nameof(callback));
            }

            _logger.LogInformation($"Sending proactive message. botAppId: {botAppId}");

            return await ProcessActivity(settings, reference.GetContinuationActivity(), callback, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Process the request from WeChat.
        /// This method can be called from inside a POST method on any Controller implementation.
        /// </summary>
        /// <param name="httpRequest">The HTTP request object, typically in a POST handler by a Controller.</param>
        /// <param name="httpResponse">The HTTP response object.</param>
        /// <param name="bot">The bot implementation.</param>
        /// <param name="secretInfo">The secret info provide by WeChat.</param>
        /// <param name="settings">WeChat settings.</param>
        /// <param name="cancellationToken">A cancellation token that can be used by other objects
        /// or threads to receive notice of cancellation.</param>
        /// <returns>A task that represents the work queued to execute.</returns>
        public async Task ProcessAsync(HttpRequest httpRequest, HttpResponse httpResponse, IBot bot, SecretInfo secretInfo, WeChatSettings settings, CancellationToken cancellationToken = default(CancellationToken))
        {
            _logger.LogInformation("Receive a new request from WeChat.");
            if (httpRequest == null)
            {
                throw new ArgumentNullException(nameof(httpRequest));
            }

            if (httpResponse == null)
            {
                throw new ArgumentNullException(nameof(httpResponse));
            }

            if (bot == null)
            {
                throw new ArgumentNullException(nameof(bot));
            }

            if (secretInfo == null)
            {
                throw new ArgumentNullException(nameof(secretInfo));
            }

            if (!VerificationHelper.VerifySignature(secretInfo.WebhookSignature, secretInfo.Timestamp, secretInfo.Nonce, settings.Token))
            {
                throw new UnauthorizedAccessException("Signature verification failed.");
            }

            // Return echo string when request is setting up the endpoint.
            if (!string.IsNullOrEmpty(secretInfo.EchoString))
            {
                await httpResponse.WriteAsync(secretInfo.EchoString, cancellationToken).ConfigureAwait(false);
                return;
            }

            // Directly return OK header to prevent WeChat from retrying.
            if (!settings.PassiveResponseMode)
            {
                httpResponse.StatusCode = (int)HttpStatusCode.OK;
                httpResponse.ContentType = "text/event-stream";
                await httpResponse.WriteAsync(string.Empty).ConfigureAwait(false);
                await httpResponse.Body.FlushAsync().ConfigureAwait(false);
            }

            try
            {
                var wechatRequest = GetRequestMessage(settings, httpRequest.Body, secretInfo);
                var wechatResponse = await ProcessWeChatRequest(
                                settings,
                                wechatRequest,
                                bot.OnTurnAsync,
                                cancellationToken).ConfigureAwait(false);

                // Reply WeChat(User) request have two ways, set response in http response or use background task to process the request async.
                if (settings.PassiveResponseMode)
                {
                    httpResponse.StatusCode = (int)HttpStatusCode.OK;
                    httpResponse.ContentType = "text/xml";
                    var xmlString = WeChatMessageFactory.ConvertResponseToXml(wechatResponse);
                    await httpResponse.WriteAsync(xmlString).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Process WeChat request failed.");
                throw;
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                // free managed resources
                if (_wechatClient != null)
                {
                    _wechatClient.Dispose();
                }
            }
        }

        /// <summary>
        /// Process the request from WeChat.
        /// </summary>
        /// <param name="settings">WeChat settings.</param>
        /// <param name="wechatRequest">Request message entity from wechat.</param>
        /// <param name="callback"> Bot callback handler.</param>
        /// <param name="cancellationToken">Cancellation Token of this Task.</param>
        /// <returns>Response message entity.</returns>
        private async Task<object> ProcessWeChatRequest(WeChatSettings settings, IRequestMessageBase wechatRequest, BotCallbackHandler callback, CancellationToken cancellationToken)
        {
            var activity = await _wechatMessageMapper.ToConnectorMessage(settings, wechatRequest).ConfigureAwait(false);
            BotAssert.ActivityNotNull(activity);
            return await ProcessActivityAsync(settings, activity, callback, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Parse the XDocument to RequestMessage, decrypt it if needed.
        /// </summary>
        /// <param name="settings">WeChat settings.</param>
        /// <param name="requestStream">WeChat RequestBody stream.</param>
        /// <param name="secretInfo">The secretInfo used to decrypt the message.</param>
        /// <returns>Decrypted WeChat RequestMessage instance.</returns>
        private IRequestMessageBase GetRequestMessage(WeChatSettings settings, Stream requestStream, SecretInfo secretInfo)
        {
            if (requestStream.CanSeek)
            {
                requestStream.Seek(0, SeekOrigin.Begin);
            }

            using (var xr = XmlReader.Create(requestStream))
            {
                var postDataDocument = XDocument.Load(xr);

                // decrypt xml document message and parse to message
                var postDataStr = postDataDocument.ToString();
                var decryptDoc = postDataDocument;

                if (secretInfo != null
                    && !string.IsNullOrWhiteSpace(settings.Token)
                    && postDataDocument.Root.Element("Encrypt") != null
                    && !string.IsNullOrEmpty(postDataDocument.Root.Element("Encrypt").Value))
                {
                    var msgCrype = new MessageCryptography(secretInfo, settings);
                    var msgXml = msgCrype.DecryptMessage(postDataStr);

                    decryptDoc = XDocument.Parse(msgXml);
                }

                var requestMessage = WeChatMessageFactory.GetRequestEntity(decryptDoc, _logger);

                return requestMessage;
            }
        }

        /// <summary>
        /// Process the activity, running bot logic and send responses to WeChat.
        /// </summary>
        /// <param name="settings">WeChat settings.</param>
        /// <param name="activity">The updated activity in the form '{id: `id of activity to update`, ...}'.</param>
        /// <param name="callback">The method to call for the resulting bot turn.</param>
        /// <param name="cancellationToken">A cancellation token that can be used by other objects
        /// or threads to receive notice of cancellation.</param>
        /// <returns>WeChat response or ChannelData bot provided.</returns>
        private async Task<object> ProcessActivityAsync(WeChatSettings settings, Activity activity, BotCallbackHandler callback, CancellationToken cancellationToken = default(CancellationToken))
        {
            using (var context = new TurnContext(this, activity))
            {
                try
                {
                    var responses = new List<Activity>();
                    context.TurnState.Add(TurnResponseKey, responses);
                    await RunPipelineAsync(context, callback, cancellationToken).ConfigureAwait(false);
                    try
                    {
                        var activities = responses;
                        if (settings.PassiveResponseMode)
                        {
                            return await ProcessBotResponse(settings, activities, activity.From.Id).ConfigureAwait(false);
                        }

                        // Running a background task, Get bot response and parse it from activity to WeChat response message
                        if (_taskQueue == null)
                        {
                            throw new NullReferenceException("Background task queue can not be null.");
                        }

                        _taskQueue.QueueBackgroundWorkItem(async (ct) =>
                        {
                            await ProcessBotResponse(settings, activities, activity.From.Id).ConfigureAwait(false);
                        });
                        return null;
                    }
                    catch (Exception e)
                    {
                        // TODO: exception handling when send message to wechat api failed.
                        _logger.LogError(e, "Failed to process bot response.");
                        throw;
                    }
                }
                catch (Exception ex)
                {
                    if (OnTurnError != null)
                    {
                        // exception handing when bot throw an exception.
                        await OnTurnError(context, ex).ConfigureAwait(false);
                        return null;
                    }

                    throw;
                }
            }
        }

        /// <summary>
        /// Process the activity and send responses to WeChat.
        /// </summary>
        /// <param name="settings">WeChat settings.</param>
        /// <param name="activity">The updated activity in the form '{id: `id of activity to update`, ...}'.</param>
        /// <param name="callback">The method to call for the resulting bot turn.</param>
        /// <param name="cancellationToken">A cancellation token that can be used by other objects
        /// or threads to receive notice of cancellation.</param>
        /// <returns>WeChat response or ChannelData bot provided.</returns>
        private async Task<object> ProcessActivity(WeChatSettings settings, Activity activity, BotCallbackHandler callback, CancellationToken cancellationToken = default(CancellationToken))
        {
            using (var context = new TurnContext(this, activity))
            {
                try
                {
                    var responses = new List<Activity>();
                    context.TurnState.Add(TurnResponseKey, responses);
                    await RunPipelineAsync(context, callback, cancellationToken).ConfigureAwait(false);
                    try
                    {
                        var activities = responses;

                        return await ProcessBotResponse(settings, activities, activity.From.Id).ConfigureAwait(false);
                    }
                    catch (Exception e)
                    {
                        // TODO: exception handling when send message to wechat api failed.
                        _logger.LogError(e, "Failed to process bot response.");
                        throw;
                    }
                }
                catch (Exception ex)
                {
                    if (OnTurnError != null)
                    {
                        // exception handing when bot throw an exception.
                        await OnTurnError(context, ex).ConfigureAwait(false);
                        return null;
                    }

                    throw;
                }
            }
        }

        /// <summary>
        /// Get the respone from bot for the wechat request.
        /// </summary>
        /// <param name="settings">WeChat settings.</param>
        /// <param name="activities">List of bot activities.</param>
        /// <param name="openId">User's open id from WeChat.</param>
        /// <returns>Bot response message.</returns>
        private async Task<object> ProcessBotResponse(WeChatSettings settings, List<Activity> activities, string openId)
        {
            object response = null;
            foreach (var activity in activities)
            {
                if (activity != null && activity.Type == ActivityTypes.Message)
                {
                    if (activity.ChannelData != null)
                    {
                        if (settings.PassiveResponseMode)
                        {
                            response = activity.ChannelData;
                        }
                        else
                        {
                            await SendMessageToWeChat(settings, activity.ChannelData).ConfigureAwait(false);
                        }
                    }
                    else
                    {
                        var resposneList = await _wechatMessageMapper.ToWeChatMessages(settings, activity).ConfigureAwait(false);

                        // Passive Response can only response one message per turn, retrun the last acitvity as the response.
                        if (settings.PassiveResponseMode)
                        {
                            response = resposneList.LastOrDefault();
                        }
                        else
                        {
                            await SendMessageToWechat(settings, resposneList, openId).ConfigureAwait(false);
                        }
                    }
                }
            }

            return response;
        }

        /// <summary>
        /// Send raw channel data to WeChat.
        /// </summary>
        /// <param name="settings">WeChat settings.</param>
        /// <param name="channelData">Raw channel data.</param>
        /// <returns>Task running result.</returns>
        private async Task SendMessageToWeChat(WeChatSettings settings, object channelData)
        {
            try
            {
                await _wechatClient.SendMessageToUser(settings, channelData).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Send channel data to WeChat failed.");
                throw;
            }
        }

        /// <summary>
        /// Send response based on message type.
        /// </summary>
        /// <param name="settings">WeChat settings.</param>
        /// <param name="responseList">Response message list.</param>
        /// <param name="openId">User's open id from WeChat.</param>
        /// <returns>Task running result.</returns>
        private async Task SendMessageToWechat(WeChatSettings settings, IList<IResponseMessageBase> responseList, string openId)
        {
            foreach (var response in responseList)
            {
                try
                {
                    switch (response.MsgType)
                    {
                        case ResponseMessageTypes.Text:
                            var textResponse = response as TextResponse;
                            await _wechatClient.SendTextAsync(settings, openId, textResponse.Content).ConfigureAwait(false);
                            break;
                        case ResponseMessageTypes.Image:
                            var imageResposne = response as ImageResponse;
                            await _wechatClient.SendImageAsync(settings, openId, imageResposne.Image.MediaId).ConfigureAwait(false);
                            break;
                        case ResponseMessageTypes.News:
                            var newsResponse = response as NewsResponse;
                            await _wechatClient.SendNewsAsync(settings, openId, newsResponse.Articles).ConfigureAwait(false);
                            break;
                        case ResponseMessageTypes.Music:
                            var musicResponse = response as MusicResponse;
                            var music = musicResponse.Music;
                            await _wechatClient.SendMusicAsync(settings, openId, music.Title, music.Description, music.MusicUrl, music.HQMusicUrl, music.ThumbMediaId).ConfigureAwait(false);
                            break;
                        case ResponseMessageTypes.MPNews:
                            var mpnewsResponse = response as MPNewsResponse;
                            await _wechatClient.SendMPNewsAsync(settings, openId, mpnewsResponse.MediaId).ConfigureAwait(false);
                            break;
                        case ResponseMessageTypes.Video:
                            var videoResposne = response as VideoResponse;
                            var video = videoResposne.Video;
                            await _wechatClient.SendVideoAsync(settings, openId, video.MediaId, video.Title, video.Description).ConfigureAwait(false);
                            break;
                        case ResponseMessageTypes.Voice:
                            var voiceResponse = response as VoiceResponse;
                            var voice = voiceResponse.Voice;
                            await _wechatClient.SendVoiceAsync(settings, openId, voice.MediaId).ConfigureAwait(false);
                            break;
                        case ResponseMessageTypes.MessageMenu:
                            var menuResponse = response as MessageMenuResponse;
                            await _wechatClient.SendMessageMenuAsync(settings, openId, menuResponse.MessageMenu).ConfigureAwait(false);
                            break;
                        case ResponseMessageTypes.LocationMessage:
                        case ResponseMessageTypes.SuccessResponse:
                        case ResponseMessageTypes.Unknown:
                        case ResponseMessageTypes.NoResponse:
                        default:
                            await _wechatClient.SendMessageToUser(settings, response).ConfigureAwait(false);
                            break;
                    }
                }
                catch (Exception e)
                {
                    _logger.LogError(e, $"Send response to WeChat failed.");
                    throw;
                }
            }
        }
    }
}
