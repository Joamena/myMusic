﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Globalization;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization; //hay q agregar "system.web.extensions" en referencias.

namespace twitterPruebas
{
    public class OAuthInfo
    {
        public string ConsumerKey { get; set; }
        public string ConsumerSecret { get; set; }
        public string AccessToken { get; set; }
        public string AccessSecret { get; set; }
    }

    public class Tweet
    {
        public long Id { get; set; }
        public DateTime CreatedAt { get; set; }
        public string UserName { get; set; }
        public string ScreenName { get; set; }
        public string Text { get; set; }
    }

    class MetodosTwitter
    {
        private readonly OAuthInfo _oauth;

        public MetodosTwitter(OAuthInfo oauth)
        {
            this._oauth = oauth;
        }

        //publica tweets!
        public void UpdateStatus(string message)
        {
            new RequestBuilder(_oauth, "POST", "https://api.twitter.com/1.1/statuses/update.json")
                .AddParameter("status", message)
                .Execute();
        }

        public IEnumerable<Tweet> GetHomeTimeline(long? sinceId = null, long? maxId = null, int? count = 20)
        {
            return GetTimeline("https://api.twitter.com/1.1/statuses/home_timeline.json", sinceId, maxId, count, "");
        }

        public IEnumerable<Tweet> GetMentions(long? sinceId = null, long? maxId = null, int? count = 20)
        {
            return GetTimeline("https://api.twitter.com/1.1/statuses/mentions.json", sinceId, maxId, count, "");
        }

        public IEnumerable<Tweet> GetUserTimeline(long? sinceId = null, long? maxId = null, int? count = 20, string screenName = "")
        {
            //
            return GetTimeline("https://api.twitter.com/1.1/statuses/user_timeline.json", sinceId, maxId, count, screenName);
        }


        private IEnumerable<Tweet> GetTimeline(string url, long? sinceId, long? maxId, int? count, string screenName)
        {
            var builder = new RequestBuilder(_oauth, "GET", url);

            if (sinceId.HasValue)
                builder.AddParameter("since_id", sinceId.Value.ToString());

            if (maxId.HasValue)
                builder.AddParameter("max_id", maxId.Value.ToString());

            if (count.HasValue)
                builder.AddParameter("count", count.Value.ToString());

            if (screenName != "")
                builder.AddParameter("screen_name", screenName);

            var responseContent = builder.Execute();

            var serializer = new JavaScriptSerializer();

            var tweets = (object[])serializer.DeserializeObject(responseContent);

            return tweets.Cast<Dictionary<string, object>>().Select(tweet =>
            {
                var user = ((Dictionary<string, object>)tweet["user"]);
                var date = DateTime.ParseExact(tweet["created_at"].ToString(),
                    "ddd MMM dd HH:mm:ss zz00 yyyy",
                    CultureInfo.InvariantCulture).ToLocalTime();

                return new Tweet
                {
                    Id = (long)tweet["id"],
                    CreatedAt = date,
                    Text = (string)tweet["text"],
                    UserName = (string)user["name"],
                    ScreenName = (string)user["screen_name"]
                };
            }).ToArray();
        }

        #region RequestBuilder

        public class RequestBuilder
        {
            private const string Version = "1.0";
            private const string SignatureMethod = "HMAC-SHA1";

            private readonly OAuthInfo _oauth;
            private readonly string _method;
            private readonly IDictionary<string, string> _customParameters;
            private readonly string _url;

            public RequestBuilder(OAuthInfo oauth, string method, string url)
            {
                this._oauth = oauth;
                this._method = method;
                this._url = url;
                _customParameters = new Dictionary<string, string>();
            }

            public RequestBuilder AddParameter(string name, string value)
            {
                _customParameters.Add(name, value.EncodeRfc3986());
                return this;
            }

            public string Execute()
            {
                var timespan = GetTimestamp();
                var nonce = CreateNonce();

                var parameters = new Dictionary<string, string>(_customParameters);
                AddOAuthParameters(parameters, timespan, nonce);

                var signature = GenerateSignature(parameters);
                var headerValue = GenerateAuthorizationHeaderValue(parameters, signature);

                var request = (HttpWebRequest)WebRequest.Create(GetRequestUrl());
                request.Method = _method;
                request.ContentType = "application/x-www-form-urlencoded";

                request.Headers.Add("Authorization", headerValue);

                WriteRequestBody(request);

                // It looks like a bug in HttpWebRequest. It throws random TimeoutExceptions
                // after some requests. Abort the request seems to work. More info: 
                // http://stackoverflow.com/questions/2252762/getrequeststream-throws-timeout-exception-randomly

                var response = request.GetResponse();

                string content;

                using (var stream = response.GetResponseStream())
                {
                    using (var reader = new StreamReader(stream))
                    {
                        content = reader.ReadToEnd();
                    }
                }

                request.Abort();

                return content;
            }

            private void WriteRequestBody(HttpWebRequest request)
            {
                if (_method == "GET")
                    return;

                var requestBody = Encoding.ASCII.GetBytes(GetCustomParametersString());
                using (var stream = request.GetRequestStream())
                    stream.Write(requestBody, 0, requestBody.Length);
            }

            private string GetRequestUrl()
            {
                if (_method != "GET" || _customParameters.Count == 0)
                    return _url;

                return string.Format("{0}?{1}", _url, GetCustomParametersString());
            }

            private string GetCustomParametersString()
            {
                return _customParameters.Select(x => string.Format("{0}={1}", x.Key, x.Value)).Join("&");
            }

            private string GenerateAuthorizationHeaderValue(IEnumerable<KeyValuePair<string, string>> parameters, string signature)
            {
                return new StringBuilder("OAuth ")
                    .Append(parameters.Concat(new KeyValuePair<string, string>("oauth_signature", signature))
                                .Where(x => x.Key.StartsWith("oauth_"))
                                .Select(x => string.Format("{0}=\"{1}\"", x.Key, x.Value.EncodeRfc3986()))
                                .Join(","))
                    .ToString();
            }

            private string GenerateSignature(IEnumerable<KeyValuePair<string, string>> parameters)
            {
                var dataToSign = new StringBuilder()
                    .Append(_method).Append("&")
                    .Append(_url.EncodeRfc3986()).Append("&")
                    .Append(parameters
                                .OrderBy(x => x.Key)
                                .Select(x => string.Format("{0}={1}", x.Key, x.Value))
                                .Join("&")
                                .EncodeRfc3986());

                var signatureKey = string.Format("{0}&{1}", _oauth.ConsumerSecret.EncodeRfc3986(), _oauth.AccessSecret.EncodeRfc3986());
                var sha1 = new HMACSHA1(Encoding.ASCII.GetBytes(signatureKey));

                var signatureBytes = sha1.ComputeHash(Encoding.ASCII.GetBytes(dataToSign.ToString()));
                return Convert.ToBase64String(signatureBytes);
            }

            private void AddOAuthParameters(IDictionary<string, string> parameters, string timestamp, string nonce)
            {
                parameters.Add("oauth_version", Version);
                parameters.Add("oauth_consumer_key", _oauth.ConsumerKey);
                parameters.Add("oauth_nonce", nonce);
                parameters.Add("oauth_signature_method", SignatureMethod);
                parameters.Add("oauth_timestamp", timestamp);
                parameters.Add("oauth_token", _oauth.AccessToken);
            }

            private static string GetTimestamp()
            {
                return ((int)(DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalSeconds).ToString();
            }

            private static string CreateNonce()
            {
                return new Random().Next(0x0000000, 0x7fffffff).ToString("X8");
            }
        }

        #endregion
    }

    public static class TinyTwitterHelperExtensions
    {
        public static string Join<T>(this IEnumerable<T> items, string separator)
        {
            return string.Join(separator, items.ToArray());
        }

        public static IEnumerable<T> Concat<T>(this IEnumerable<T> items, T value)
        {
            return items.Concat(new[] { value });
        }

        public static string EncodeRfc3986(this string value)
        {
            // From Twitterizer http://www.twitterizer.net/

            if (string.IsNullOrEmpty(value))
                return string.Empty;

            var encoded = Uri.EscapeDataString(value);

            return Regex
                .Replace(encoded, "(%[0-9a-f][0-9a-f])", c => c.Value.ToUpper())
                .Replace("(", "%28")
                .Replace(")", "%29")
                .Replace("$", "%24")
                .Replace("!", "%21")
                .Replace("*", "%2A")
                .Replace("'", "%27")
                .Replace("%7E", "~");
        }
    }

}