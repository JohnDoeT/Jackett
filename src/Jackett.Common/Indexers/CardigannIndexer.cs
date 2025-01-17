﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;
using Jackett.Common.Helpers;
using Jackett.Common.Models;
using Jackett.Common.Models.IndexerConfig;
using static Jackett.Common.Models.IndexerConfig.ConfigurationData;
using Jackett.Common.Services.Interfaces;
using Jackett.Common.Utils;
using Jackett.Common.Utils.Clients;
using Newtonsoft.Json.Linq;
using NLog;
using System.Globalization;
using System.Web;

namespace Jackett.Common.Indexers
{
    public class CardigannIndexer : BaseWebIndexer
    {
        protected IndexerDefinition Definition;
        public override string ID { get { return (Definition != null ? Definition.Site : GetIndexerID(GetType())); } }

        protected WebClientStringResult landingResult;
        protected IHtmlDocument landingResultDocument;

        protected List<string> DefaultCategories = new List<string>();

        new ConfigurationData configData
        {
            get { return (ConfigurationData)base.configData; }
            set { base.configData = value; }
        }

        protected readonly string[] OptionalFileds = new string[] { "imdb", "rageid", "tvdbid", "banner" };

        public CardigannIndexer(IIndexerConfigurationService configService, Utils.Clients.WebClient wc, Logger l, IProtectionService ps, IndexerDefinition Definition)
            : base(configService: configService,
                   client: wc,
                   logger: l,
                   p: ps)
        {
            this.Definition = Definition;

            // Add default data if necessary
            if (Definition.Settings == null)
            {
                Definition.Settings = new List<settingsField>();
                Definition.Settings.Add(new settingsField { Name = "username", Label = "Username", Type = "text" });
                Definition.Settings.Add(new settingsField { Name = "password", Label = "Password", Type = "password" });
            }

            if (Definition.Encoding == null)
                Definition.Encoding = "UTF-8";

            if (Definition.Login != null && Definition.Login.Method == null)
                Definition.Login.Method = "form";

            if (Definition.Search.Paths == null)
            {
                Definition.Search.Paths = new List<searchPathBlock>();
            }

            // convert definitions with a single search Path to a Paths entry
            if (Definition.Search.Path != null)
            {
                var legacySearchPath = new searchPathBlock();
                legacySearchPath.Path = Definition.Search.Path;
                legacySearchPath.Inheritinputs = true;
                Definition.Search.Paths.Add(legacySearchPath);
            }

            // init missing mandatory attributes
            DisplayName = Definition.Name;
            DisplayDescription = Definition.Description;
            if (Definition.Links.Count > 1)
                AlternativeSiteLinks = Definition.Links.ToArray();
            DefaultSiteLink = Definition.Links[0];
            if (Definition.Legacylinks != null)
                LegacySiteLinks = Definition.Legacylinks.ToArray();
            Encoding = Encoding.GetEncoding(Definition.Encoding);
            if (!DefaultSiteLink.EndsWith("/"))
                DefaultSiteLink += "/";
            Language = Definition.Language;
            Type = Definition.Type;
            TorznabCaps = new TorznabCapabilities();

            TorznabCaps.SupportsImdbMovieSearch = Definition.Caps.Modes.Where(c => c.Key == "movie-search" && c.Value.Contains("imdbid")).Any();
            if (Definition.Caps.Modes.ContainsKey("music-search"))
                TorznabCaps.SupportedMusicSearchParamsList = Definition.Caps.Modes["music-search"];

            // init config Data
            configData = new ConfigurationData();
            foreach (var Setting in Definition.Settings)
            {
                Item item;

                if (Setting.Type != null)
                {
                    switch (Setting.Type)
                    {
                        case "checkbox":
                            item = new BoolItem { Value = false };

                            if (Setting.Default != null && Setting.Default == "true")
                            {
                                ((BoolItem)item).Value = true;
                            }
                            break;
                        case "password":
                        case "text":
                            item = new StringItem { Value = Setting.Default };
                            break;
                        case "select":
                            if (Setting.Options == null)
                            {
                                throw new Exception("Options must be given for the 'select' type.");
                            }

                            item = new SelectItem(Setting.Options) { Value = Setting.Default };
                            break;
                        case "info":
                            item = new DisplayItem(Setting.Default);
                            break;
                        default:
                            throw new Exception($"Invalid setting type '{Setting.Type}' specified.");
                    }
                }
                else
                {
                    item = new StringItem { Value = Setting.Default }; ;
                }

                item.Name = Setting.Label;
                if (item.Name == null)
                    item.Name = Setting.Name;
                configData.AddDynamic(Setting.Name, item);
            }

            if (Definition.Caps.Categories != null)
            {
                foreach (var Category in Definition.Caps.Categories)
                {
                    var cat = TorznabCatType.GetCatByName(Category.Value);
                    if (cat == null)
                    {
                        logger.Error(string.Format("CardigannIndexer ({0}): invalid Torznab category for id {1}: {2}", ID, Category.Key, Category.Value));
                        continue;
                    }
                    AddCategoryMapping(Category.Key, cat);
                }
            }

            if (Definition.Caps.Categorymappings != null)
            {
                foreach (var Categorymapping in Definition.Caps.Categorymappings)
                {
                    TorznabCategory TorznabCat = null;

                    if (Categorymapping.cat != null)
                    {
                        TorznabCat = TorznabCatType.GetCatByName(Categorymapping.cat);
                        if (TorznabCat == null)
                        {
                            logger.Error(string.Format("CardigannIndexer ({0}): invalid Torznab category for id {1}: {2}", ID, Categorymapping.id, Categorymapping.cat));
                            continue;
                        }
                    }
                    AddCategoryMapping(Categorymapping.id, TorznabCat, Categorymapping.desc, Categorymapping.raw);
                    if (Categorymapping.Default)
                        DefaultCategories.Add(Categorymapping.id);
                }
            }
            LoadValuesFromJson(null);
        }

        public override void LoadValuesFromJson(JToken jsonConfig, bool useProtectionService = false)
        {
            base.LoadValuesFromJson(jsonConfig, useProtectionService);

            // add self signed cert to trusted certs
            if (Definition.Certificates != null)
            {
                foreach (var certificateHash in Definition.Certificates)
                    webclient.AddTrustedCertificate(new Uri(SiteLink).Host, certificateHash);
            }
        }

        protected Dictionary<string, object> getTemplateVariablesFromConfigData()
        {
            Dictionary<string, object> variables = new Dictionary<string, object>();

            variables[".Config.sitelink"] = SiteLink;
            foreach (settingsField Setting in Definition.Settings)
            {
                string value;
                var item = configData.GetDynamic(Setting.Name);
                if (item.GetType() == typeof(BoolItem))
                {
                    value = (((BoolItem)item).Value == true ? "true" : "");
                }
                else if (item.GetType() == typeof(SelectItem))
                {
                    value = ((SelectItem)item).Value;
                }
                else
                {
                    value = ((StringItem)item).Value;
                }
                variables[".Config." + Setting.Name] = value;
            }
            return variables;
        }

        // A very bad implementation of the golang template/text templating engine.
        // But it should work for most basic constucts used by Cardigann definitions.
        protected delegate string TemplateTextModifier(string str);
        protected string applyGoTemplateText(string template, Dictionary<string, object> variables = null, TemplateTextModifier modifier = null)
        {
            if (variables == null)
            {
                variables = getTemplateVariablesFromConfigData();
            }

            // handle re_replace expression
            // Example: {{ re_replace .Query.Keywords "[^a-zA-Z0-9]+" "%" }}
            Regex ReReplaceRegex = new Regex(@"{{\s*re_replace\s+(\..+?)\s+""(.*?)""\s+""(.*?)""\s*}}");
            var ReReplaceRegexMatches = ReReplaceRegex.Match(template);

            while (ReReplaceRegexMatches.Success)
            {
                string all = ReReplaceRegexMatches.Groups[0].Value;
                string variable = ReReplaceRegexMatches.Groups[1].Value;
                string regexp = ReReplaceRegexMatches.Groups[2].Value;
                string newvalue = ReReplaceRegexMatches.Groups[3].Value;

                Regex ReplaceRegex = new Regex(regexp);
                var input = (string)variables[variable];
                var expanded = ReplaceRegex.Replace(input, newvalue);

                if (modifier != null)
                    expanded = modifier(expanded);

                template = template.Replace(all, expanded);
                ReReplaceRegexMatches = ReReplaceRegexMatches.NextMatch();
            }

            // handle join expression
            // Example: {{ join .Categories "," }}
            Regex JoinRegex = new Regex(@"{{\s*join\s+(\..+?)\s+""(.*?)""\s*}}");
            var JoinMatches = JoinRegex.Match(template);

            while (JoinMatches.Success)
            {
                string all = JoinMatches.Groups[0].Value;
                string variable = JoinMatches.Groups[1].Value;
                string delimiter = JoinMatches.Groups[2].Value;

                var input = (ICollection<string>)variables[variable];
                var expanded = string.Join(delimiter, input);

                if (modifier != null)
                    expanded = modifier(expanded);

                template = template.Replace(all, expanded);
                JoinMatches = JoinMatches.NextMatch();
            }

            // handle if ... else ... expression
            Regex IfElseRegex = new Regex(@"{{\s*if\s*(.+?)\s*}}(.*?){{\s*else\s*}}(.*?){{\s*end\s*}}");
            var IfElseRegexMatches = IfElseRegex.Match(template);

            while (IfElseRegexMatches.Success)
            {
                string conditionResult = null;

                string all = IfElseRegexMatches.Groups[0].Value;
                string condition = IfElseRegexMatches.Groups[1].Value;
                string onTrue = IfElseRegexMatches.Groups[2].Value;
                string onFalse = IfElseRegexMatches.Groups[3].Value;

                if (condition.StartsWith("."))
                {
                    var conditionResultState = false;
                    var value = variables[condition];

                    if (value == null)
                        conditionResultState = false;
                    else if (value is string)
                        conditionResultState = !string.IsNullOrWhiteSpace((string)value);
                    else if (value is ICollection)
                        conditionResultState = ((ICollection)value).Count > 0;
                    else
                        throw new Exception(string.Format("Unexpceted type for variable {0}: {1}", condition, value.GetType()));

                    if (conditionResultState)
                    {
                        conditionResult = onTrue;
                    }
                    else
                    {
                        conditionResult = onFalse;
                    }
                }
                else
                {
                    throw new NotImplementedException("CardigannIndexer: Condition operation '" + condition + "' not implemented");
                }
                template = template.Replace(all, conditionResult);
                IfElseRegexMatches = IfElseRegexMatches.NextMatch();
            }

            // handle range expression
            Regex RangeRegex = new Regex(@"{{\s*range\s*(.+?)\s*}}(.*?){{\.}}(.*?){{end}}");
            var RangeRegexMatches = RangeRegex.Match(template);

            while (RangeRegexMatches.Success)
            {
                string expanded = string.Empty;

                string all = RangeRegexMatches.Groups[0].Value;
                string variable = RangeRegexMatches.Groups[1].Value;
                string prefix = RangeRegexMatches.Groups[2].Value;
                string postfix = RangeRegexMatches.Groups[3].Value;

                foreach (string value in (ICollection<string>)variables[variable])
                {
                    var newvalue = value;
                    if (modifier != null)
                        newvalue = modifier(newvalue);
                    expanded += prefix + newvalue + postfix;
                }
                template = template.Replace(all, expanded);
                RangeRegexMatches = RangeRegexMatches.NextMatch();
            }

            // handle simple variables
            Regex VariablesRegEx = new Regex(@"{{\s*(\..+?)\s*}}");
            var VariablesRegExMatches = VariablesRegEx.Match(template);

            while (VariablesRegExMatches.Success)
            {
                string expanded = string.Empty;

                string all = VariablesRegExMatches.Groups[0].Value;
                string variable = VariablesRegExMatches.Groups[1].Value;

                string value = (string)variables[variable];
                if (modifier != null)
                    value = modifier(value);
                template = template.Replace(all, value);
                VariablesRegExMatches = VariablesRegExMatches.NextMatch();
            }

            return template;
        }

        protected bool checkForError(WebClientStringResult loginResult, IList<errorBlock> errorBlocks)
        {
            if(loginResult.Status == HttpStatusCode.Unauthorized) // e.g. used by YGGtorrent
                throw new ExceptionWithConfigData("401 Unauthorized, check your credentials", configData);

            if (errorBlocks == null)
                return true; // no error

            var ResultParser = new HtmlParser();
            var ResultDocument = ResultParser.ParseDocument(loginResult.Content);
            foreach (errorBlock error in errorBlocks)
            {
                var selection = ResultDocument.QuerySelector(error.Selector);
                if (selection != null)
                {
                    string errorMessage = selection.TextContent;
                    if (error.Message != null)
                    {
                        errorMessage = handleSelector(error.Message, ResultDocument.FirstElementChild);
                    }
                    throw new ExceptionWithConfigData(string.Format("Error: {0}", errorMessage.Trim()), configData);
                }
            }
            return true; // no error
        }

        protected async Task<bool> DoLogin()
        {
            var Login = Definition.Login;

            if (Login == null)
                return true;

            if (Login.Method == "post")
            {
                var pairs = new Dictionary<string, string>();
                foreach (var Input in Definition.Login.Inputs)
                {
                    var value = applyGoTemplateText(Input.Value);
                    pairs.Add(Input.Key, value);
                }

                var LoginUrl = resolvePath(Login.Path).ToString();
                configData.CookieHeader.Value = null;
                var loginResult = await RequestLoginAndFollowRedirect(LoginUrl, pairs, null, true, null, SiteLink, true);
                configData.CookieHeader.Value = loginResult.Cookies;

                checkForError(loginResult, Definition.Login.Error);
            }
            else if (Login.Method == "form")
            {
                var LoginUrl = resolvePath(Login.Path).ToString();

                var queryCollection = new NameValueCollection();
                var pairs = new Dictionary<string, string>();

                var CaptchaConfigItem = (RecaptchaItem)configData.GetDynamic("Captcha");

                if (CaptchaConfigItem != null)
                {
                    if (!string.IsNullOrWhiteSpace(CaptchaConfigItem.Cookie))
                    {
                        // for remote users just set the cookie and return
                        CookieHeader = CaptchaConfigItem.Cookie;
                        return true;
                    }

                    var CloudFlareCaptchaChallenge = landingResultDocument.QuerySelector("script[src=\"/cdn-cgi/scripts/cf.challenge.js\"]");
                    if (CloudFlareCaptchaChallenge != null)
                    {
                        var CloudFlareQueryCollection = new NameValueCollection();
                        CloudFlareQueryCollection["id"] = CloudFlareCaptchaChallenge.GetAttribute("data-ray");

                        CloudFlareQueryCollection["g-recaptcha-response"] = CaptchaConfigItem.Value;
                        var ClearanceUrl = resolvePath("/cdn-cgi/l/chk_captcha?" + CloudFlareQueryCollection.GetQueryString());

                        var ClearanceResult = await RequestStringWithCookies(ClearanceUrl.ToString(), null, SiteLink);

                        if (ClearanceResult.IsRedirect) // clearance successfull
                        {
                            // request real login page again
                            landingResult = await RequestStringWithCookies(LoginUrl, null, SiteLink);
                            var htmlParser = new HtmlParser();
                            landingResultDocument = htmlParser.ParseDocument(landingResult.Content);
                        }
                        else
                        {
                            throw new ExceptionWithConfigData(string.Format("Login failed: Cloudflare clearance failed using cookies {0}: {1}", CookieHeader, ClearanceResult.Content), configData);
                        }
                    }
                    else
                    {
                        pairs.Add("g-recaptcha-response", CaptchaConfigItem.Value);
                    }
                }

                var FormSelector = Login.Form;
                if (FormSelector == null)
                    FormSelector = "form";

                // landingResultDocument might not be initiated if the login is caused by a relogin during a query
                if (landingResultDocument == null)
                {
                    var ConfigurationResult = await GetConfigurationForSetup(true);
                    if (ConfigurationResult == null) // got captcha
                    {
                        return false;
                    }
                }

                var form = landingResultDocument.QuerySelector(FormSelector);
                if (form == null)
                {
                    throw new ExceptionWithConfigData(string.Format("Login failed: No form found on {0} using form selector {1}", LoginUrl, FormSelector), configData);
                }

                var inputs = form.QuerySelectorAll("input");
                if (inputs == null)
                {
                    throw new ExceptionWithConfigData(string.Format("Login failed: No inputs found on {0} using form selector {1}", LoginUrl, FormSelector), configData);
                }

                var submitUrlstr = form.GetAttribute("action");
                if (Login.Submitpath != null)
                    submitUrlstr = Login.Submitpath;

                foreach (var input in inputs)
                {
                    var name = input.GetAttribute("name");
                    if (name == null)
                        continue;

                    var value = input.GetAttribute("value");
                    if (value == null)
                        value = "";

                    pairs[name] = value;
                }

                foreach (var Input in Definition.Login.Inputs)
                {
                    var value = applyGoTemplateText(Input.Value);
                    var input = Input.Key;
                    if (Login.Selectors)
                    {
                        var inputElement = landingResultDocument.QuerySelector(Input.Key);
                        if (inputElement == null)
                            throw new ExceptionWithConfigData(string.Format("Login failed: No input found using selector {0}", Input.Key), configData);
                        input = inputElement.GetAttribute("name");
                    }
                    pairs[input] = value;
                }

                // selector inputs
                if (Login.Selectorinputs != null)
                {
                    foreach (var Selectorinput in Login.Selectorinputs)
                    {
                        string value = null;
                        try
                        {
                            value = handleSelector(Selectorinput.Value, landingResultDocument.FirstElementChild);
                            pairs[Selectorinput.Key] = value;
                        }
                        catch (Exception ex)
                        {
                            throw new Exception(string.Format("Error while parsing selector input={0}, selector={1}, value={2}: {3}", Selectorinput.Key, Selectorinput.Value.Selector, value, ex.Message));
                        }
                    }
                }

                // getselector inputs
                if (Login.Getselectorinputs != null)
                {
                    foreach (var Selectorinput in Login.Getselectorinputs)
                    {
                        string value = null;
                        try
                        {
                            value = handleSelector(Selectorinput.Value, landingResultDocument.FirstElementChild);
                            queryCollection[Selectorinput.Key] = value;
                        }
                        catch (Exception ex)
                        {
                            throw new Exception(string.Format("Error while parsing get selector input={0}, selector={1}, value={2}: {3}", Selectorinput.Key, Selectorinput.Value.Selector, value, ex.Message));
                        }
                    }
                }
                if (queryCollection.Count > 0)
                    submitUrlstr += "?" + queryCollection.GetQueryString();
                var submitUrl = resolvePath(submitUrlstr, new Uri(LoginUrl));

                // automatically solve simpleCaptchas, if used
                var simpleCaptchaPresent = landingResultDocument.QuerySelector("script[src*=\"simpleCaptcha\"]");
                if (simpleCaptchaPresent != null)
                {
                    var captchaUrl = resolvePath("simpleCaptcha.php?numImages=1");
                    var simpleCaptchaResult = await RequestStringWithCookies(captchaUrl.ToString(), null, LoginUrl);
                    var simpleCaptchaJSON = JObject.Parse(simpleCaptchaResult.Content);
                    var captchaSelection = simpleCaptchaJSON["images"][0]["hash"].ToString();
                    pairs["captchaSelection"] = captchaSelection;
                    pairs["submitme"] = "X";
                }

                if (Login.Captcha != null)
                {
                    var Captcha = Login.Captcha;
                    if (Captcha.Type == "image")
                    {
                        var CaptchaText = (StringItem)configData.GetDynamic("CaptchaText");
                        if (CaptchaText != null)
                        {
                            var input = Captcha.Input;
                            if (Login.Selectors)
                            {
                                var inputElement = landingResultDocument.QuerySelector(Captcha.Input);
                                if (inputElement == null)
                                    throw new ExceptionWithConfigData(string.Format("Login failed: No captcha input found using {0}", Captcha.Input), configData);
                                input = inputElement.GetAttribute("name");
                            }
                            pairs[input] = CaptchaText.Value;
                        }
                    }
                    if (Captcha.Type == "text")
                    {
                        var CaptchaAnswer = (StringItem)configData.GetDynamic("CaptchaAnswer");
                        if (CaptchaAnswer != null)
                        {
                            var input = Captcha.Input;
                            if (Login.Selectors)
                            {
                                var inputElement = landingResultDocument.QuerySelector(Captcha.Input);
                                if (inputElement == null)
                                    throw new ExceptionWithConfigData(string.Format("Login failed: No captcha input found using {0}", Captcha.Input), configData);
                                input = inputElement.GetAttribute("name");
                            }
                            pairs[input] = CaptchaAnswer.Value;
                        }
                    }
                }

                // clear landingResults/Document, otherwise we might use an old version for a new relogin (if GetConfigurationForSetup() wasn't called before)
                landingResult = null;
                landingResultDocument = null;

                WebClientStringResult loginResult = null;
                var enctype = form.GetAttribute("enctype");
                if (enctype == "multipart/form-data")
                {
                    var headers = new Dictionary<string, string>();
                    var boundary = "---------------------------" + (DateTime.UtcNow.Subtract(new DateTime(1970, 1, 1))).TotalSeconds.ToString().Replace(".", "");
                    var bodyParts = new List<string>();

                    foreach (var pair in pairs)
                    {
                        var part = "--" + boundary + "\r\n" +
                          "Content-Disposition: form-data; name=\"" + pair.Key + "\"\r\n" +
                          "\r\n" +
                          pair.Value;
                        bodyParts.Add(part);
                    }

                    bodyParts.Add("--" + boundary + "--");

                    headers.Add("Content-Type", "multipart/form-data; boundary=" + boundary);
                    var body = string.Join("\r\n", bodyParts);
                    loginResult = await PostDataWithCookies(submitUrl.ToString(), pairs, configData.CookieHeader.Value, SiteLink, headers, body);
                }
                else
                {
                    loginResult = await RequestLoginAndFollowRedirect(submitUrl.ToString(), pairs, configData.CookieHeader.Value, true, null, LoginUrl, true);
                }

                configData.CookieHeader.Value = loginResult.Cookies;

                checkForError(loginResult, Definition.Login.Error);
            }
            else if (Login.Method == "cookie")
            {
                configData.CookieHeader.Value = ((StringItem)configData.GetDynamic("cookie")).Value;
            }
            else if (Login.Method == "get")
            {
                var queryCollection = new NameValueCollection();
                foreach (var Input in Definition.Login.Inputs)
                {
                    var value = applyGoTemplateText(Input.Value);
                    queryCollection.Add(Input.Key, value);
                }

                var LoginUrl = resolvePath(Login.Path + "?" + queryCollection.GetQueryString()).ToString();
                configData.CookieHeader.Value = null;
                var loginResult = await RequestStringWithCookies(LoginUrl, null, SiteLink);
                configData.CookieHeader.Value = loginResult.Cookies;

                checkForError(loginResult, Definition.Login.Error);
            }
            else if (Login.Method == "oneurl")
            {
                var OneUrl = applyGoTemplateText(Definition.Login.Inputs["oneurl"]);
                var LoginUrl = resolvePath(Login.Path + OneUrl).ToString();
                configData.CookieHeader.Value = null;
                var loginResult = await RequestStringWithCookies(LoginUrl, null, SiteLink);
                configData.CookieHeader.Value = loginResult.Cookies;

                checkForError(loginResult, Definition.Login.Error);
            }
            else
            {
                throw new NotImplementedException("Login method " + Definition.Login.Method + " not implemented");
            }
            logger.Debug(string.Format("CardigannIndexer ({0}): Cookies after login: {1}", ID, CookieHeader));
            return true;
        }

        protected string getRedirectDomainHint(string requestUrl, string RedirectUrl)
        {
            if (requestUrl.StartsWith(SiteLink) && !RedirectUrl.StartsWith(SiteLink))
            {
                var uri = new Uri(RedirectUrl);
                return uri.Scheme + "://" + uri.Host + "/";
            }
            return null;
        }

        protected string getRedirectDomainHint(WebClientByteResult result)
        {
            return getRedirectDomainHint(result.Request.Url, result.RedirectingTo);
        }

        protected string getRedirectDomainHint(WebClientStringResult result)
        {
            return getRedirectDomainHint(result.Request.Url, result.RedirectingTo);
        }

        protected async Task<bool> TestLogin()
        {
            var Login = Definition.Login;

            if (Login == null || Login.Test == null)
                return false;

            // test if login was successful
            var LoginTestUrl = resolvePath(Login.Test.Path).ToString();
            var testResult = await RequestStringWithCookies(LoginTestUrl);

            if (testResult.IsRedirect)
            {
                var errormessage = "Login Failed, got redirected.";
                var DomainHint = getRedirectDomainHint(testResult);
                if (DomainHint != null)
                {
                    errormessage += " Try changing the indexer URL to " + DomainHint + ".";
                    if (Definition.Followredirect)
                    {
                        configData.SiteLink.Value = DomainHint;
                        SiteLink = configData.SiteLink.Value;
                        SaveConfig();
                        errormessage += " Updated site link, please try again.";
                    }
                }
                throw new ExceptionWithConfigData(errormessage, configData);
            }

            if (Login.Test.Selector != null)
            {
                var testResultParser = new HtmlParser();
                var testResultDocument = testResultParser.ParseDocument(testResult.Content);
                var selection = testResultDocument.QuerySelectorAll(Login.Test.Selector);
                if (selection.Length == 0)
                {
                    throw new ExceptionWithConfigData(string.Format("Login failed: Selector \"{0}\" didn't match", Login.Test.Selector), configData);
                }
            }
            return true;
        }

        protected bool CheckIfLoginIsNeeded(WebClientStringResult Result, IHtmlDocument document)
        {
            if (Result.IsRedirect)
            {
                var DomainHint = getRedirectDomainHint(Result);
                if (DomainHint != null)
                {
                    var errormessage = "Got redirected to another domain. Try changing the indexer URL to " + DomainHint + ".";
                    if (Definition.Followredirect)
                    {
                        configData.SiteLink.Value = DomainHint;
                        SiteLink = configData.SiteLink.Value;
                        SaveConfig();
                        errormessage += " Updated site link, please try again.";
                    }
                    throw new ExceptionWithConfigData(errormessage, configData);
                }

                return true;
            }

            if (Definition.Login == null || Definition.Login.Test == null)
                return false;

            if (Definition.Login.Test.Selector != null)
            {
                var selection = document.QuerySelectorAll(Definition.Login.Test.Selector);
                if (selection.Length == 0)
                {
                    return true;
                }
            }
            return false;
        }

        public override async Task<ConfigurationData> GetConfigurationForSetup()
        {
            return await GetConfigurationForSetup(false);
        }

        public async Task<ConfigurationData> GetConfigurationForSetup(bool automaticlogin)
        {
            var Login = Definition.Login;

            if (Login == null || Login.Method != "form")
                return configData;

            var LoginUrl = resolvePath(Login.Path);

            configData.CookieHeader.Value = null;
            if (Login.Cookies != null)
                configData.CookieHeader.Value = String.Join("; ", Login.Cookies);
            landingResult = await RequestStringWithCookies(LoginUrl.AbsoluteUri, null, SiteLink);

            var htmlParser = new HtmlParser();
            landingResultDocument = htmlParser.ParseDocument(landingResult.Content);

            var hasCaptcha = false;

            var cloudFlareCaptchaScript = landingResultDocument.QuerySelector("script[src*=\"/recaptcha/api.js\"]");
            var cloudFlareCaptchaGroup = landingResultDocument.QuerySelector("#recaptca_group");
            var cloudFlareCaptchaDisplay = true;
            if (cloudFlareCaptchaGroup != null)
            {
                var cloudFlareCaptchaGroupStyle = cloudFlareCaptchaGroup.GetAttribute("style");
                if (cloudFlareCaptchaGroupStyle != null)
                    cloudFlareCaptchaDisplay = !cloudFlareCaptchaGroupStyle.Contains("display:none;");
            }
            var grecaptcha = landingResultDocument.QuerySelector(".g-recaptcha");
            if (cloudFlareCaptchaScript != null && grecaptcha != null && cloudFlareCaptchaDisplay)
            {
                hasCaptcha = true;
                var CaptchaItem = new RecaptchaItem();
                CaptchaItem.Name = "Captcha";
                CaptchaItem.Version = "2";
                CaptchaItem.SiteKey = grecaptcha.GetAttribute("data-sitekey");
                if (CaptchaItem.SiteKey == null) // some sites don't store the sitekey in the .g-recaptcha div (e.g. cloudflare captcha challenge page)
                    CaptchaItem.SiteKey = landingResultDocument.QuerySelector("[data-sitekey]").GetAttribute("data-sitekey");

                configData.AddDynamic("Captcha", CaptchaItem);
            }

            if (Login.Captcha != null)
            {
                var Captcha = Login.Captcha;
                if (Captcha.Type == "image")
                {
                    var captchaElement = landingResultDocument.QuerySelector(Captcha.Selector);
                    if (captchaElement != null)
                    {
                        hasCaptcha = true;

                        var CaptchaUrl = resolvePath(captchaElement.GetAttribute("src"), LoginUrl);
                        var captchaImageData = await RequestBytesWithCookies(CaptchaUrl.ToString(), landingResult.Cookies, RequestType.GET, LoginUrl.AbsoluteUri);
                        var CaptchaImage = new ImageItem { Name = "Captcha Image" };
                        var CaptchaText = new StringItem { Name = "Captcha Text" };

                        CaptchaImage.Value = captchaImageData.Content;

                        configData.AddDynamic("CaptchaImage", CaptchaImage);
                        configData.AddDynamic("CaptchaText", CaptchaText);
                    }
                    else
                    {
                        logger.Debug(string.Format("CardigannIndexer ({0}): No captcha image found", ID));
                    }
                }
                else if (Captcha.Type == "text")
                {
                    var captchaElement = landingResultDocument.QuerySelector(Captcha.Selector);
                    if (captchaElement != null)
                    {
                        hasCaptcha = true;

                        var CaptchaChallenge = new DisplayItem(captchaElement.TextContent) { Name = "Captcha Challenge" };
                        var CaptchaAnswer = new StringItem { Name = "Captcha Answer" };

                        configData.AddDynamic("CaptchaChallenge", CaptchaChallenge);
                        configData.AddDynamic("CaptchaAnswer", CaptchaAnswer);
                    }
                    else
                    {
                        logger.Debug(string.Format("CardigannIndexer ({0}): No captcha image found", ID));
                    }
                }
                else
                {
                    throw new NotImplementedException(string.Format("Captcha type \"{0}\" is not implemented", Captcha.Type));
                }
            }

            if (hasCaptcha && automaticlogin)
            {
                configData.LastError.Value = "Got captcha during automatic login, please reconfigure manually";
                logger.Error(string.Format("CardigannIndexer ({0}): Found captcha during automatic login, aborting", ID));
                return null;
            }

            return configData;
        }

        public override async Task<IndexerConfigurationStatus> ApplyConfiguration(JToken configJson)
        {
            LoadValuesFromJson(configJson);

            await DoLogin();
            await TestLogin();

            IsConfigured = true;
            SaveConfig();
            return IndexerConfigurationStatus.Completed;
        }

        protected string applyFilters(string Data, List<filterBlock> Filters, Dictionary<string, object> variables = null)
        {
            if (Filters == null)
                return Data;

            foreach (filterBlock Filter in Filters)
            {
                switch (Filter.Name)
                {
                    case "querystring":
                        var param = (string)Filter.Args;
                        Data = ParseUtil.GetArgumentFromQueryString(Data, param);
                        break;
                    case "timeparse":
                    case "dateparse":
                        var layout = (string)Filter.Args;
                        try
                        {
                            var Date = DateTimeUtil.ParseDateTimeGoLang(Data, layout);
                            Data = Date.ToString(DateTimeUtil.RFC1123ZPattern, CultureInfo.InvariantCulture);
                        }
                        catch (FormatException ex)
                        {
                            logger.Debug(ex.Message);
                        }
                        break;
                    case "regexp":
                        var pattern = (string)Filter.Args;
                        var Regexp = new Regex(pattern);
                        var Match = Regexp.Match(Data);
                        Data = Match.Groups[1].Value;
                        break;
                    case "re_replace":
                        var regexpreplace_pattern = (string)Filter.Args[0];
                        var regexpreplace_replacement = (string)Filter.Args[1];
                        regexpreplace_replacement = applyGoTemplateText(regexpreplace_replacement, variables);
                        Regex regexpreplace_regex = new Regex(regexpreplace_pattern);
                        Data = regexpreplace_regex.Replace(Data, regexpreplace_replacement);
                        break;
                    case "split":
                        var sep = (string)Filter.Args[0];
                        var pos = (string)Filter.Args[1];
                        var posInt = int.Parse(pos);
                        var strParts = Data.Split(sep[0]);
                        if (posInt < 0)
                        {
                            posInt += strParts.Length;
                        }
                        Data = strParts[posInt];
                        break;
                    case "replace":
                        var from = (string)Filter.Args[0];
                        var to = (string)Filter.Args[1];
                        to = applyGoTemplateText(to, variables);
                        Data = Data.Replace(from, to);
                        break;
                    case "trim":
                        var cutset = (string)Filter.Args;
                        if (cutset != null)
                            Data = Data.Trim(cutset[0]);
                        else
                            Data = Data.Trim();
                        break;
                    case "prepend":
                        var prependstr = (string)Filter.Args;
                        Data = applyGoTemplateText(prependstr, variables) + Data;
                        break;
                    case "append":
                        var str = (string)Filter.Args;
                        Data += applyGoTemplateText(str, variables);
                        break;
                    case "tolower":
                        Data = Data.ToLower();
                        break;
                    case "toupper":
                        Data = Data.ToUpper();
                        break;
                    case "urldecode":
                        Data = WebUtilityHelpers.UrlDecode(Data, Encoding);
                        break;
                    case "urlencode":
                        Data = WebUtilityHelpers.UrlEncode(Data, Encoding);
                        break;
                    case "timeago":
                    case "reltime":
                        Data = DateTimeUtil.FromTimeAgo(Data).ToString(DateTimeUtil.RFC1123ZPattern, CultureInfo.InvariantCulture);
                        break;
                    case "fuzzytime":
                        Data = DateTimeUtil.FromUnknown(Data).ToString(DateTimeUtil.RFC1123ZPattern, CultureInfo.InvariantCulture);
                        break;
                    case "validfilename":
                        Data = StringUtil.MakeValidFileName(Data, '_', false);
                        break;
                    case "diacritics":
                        var diacriticsOp = (string)Filter.Args;
                        if (diacriticsOp == "replace")
                        {
                            // Should replace diacritics charcaters with their base character
                            // It's not perfect, e.g. "ŠĐĆŽ - šđčćž" becomes "SĐCZ-sđccz"
                            string stFormD = Data.Normalize(NormalizationForm.FormD);
                            int len = stFormD.Length;
                            StringBuilder sb = new StringBuilder();
                            for (int i = 0; i < len; i++)
                            {
                                System.Globalization.UnicodeCategory uc = System.Globalization.CharUnicodeInfo.GetUnicodeCategory(stFormD[i]);
                                if (uc != System.Globalization.UnicodeCategory.NonSpacingMark)
                                {
                                    sb.Append(stFormD[i]);
                                }
                            }
                            Data = (sb.ToString().Normalize(NormalizationForm.FormC));
                        }
                        else
                            throw new Exception("unsupported diacritics filter argument");
                        break;
                    case "jsonjoinarray":
                        var jsonjoinarrayJSONPath = (string)Filter.Args[0];
                        var jsonjoinarraySeparator = (string)Filter.Args[1];
                        var jsonjoinarrayO = JObject.Parse(Data);
                        var jsonjoinarrayOResult = jsonjoinarrayO.SelectToken(jsonjoinarrayJSONPath);
                        var jsonjoinarrayOResultStrings = jsonjoinarrayOResult.Select(j => j.ToString());
                        Data = string.Join(jsonjoinarraySeparator, jsonjoinarrayOResultStrings);
                        break;
                    case "hexdump":
                        // this is mainly for debugging invisible special char related issues
                        var HexData = string.Join("", Data.Select(c => c + "(" + ((int)c).ToString("X2") + ")"));
                        logger.Debug(string.Format("CardigannIndexer ({0}): strdump: {1}", ID, HexData));
                        break;
                    case "strdump":
                        // for debugging
                        var DebugData = Data.Replace("\r", "\\r").Replace("\n", "\\n").Replace("\xA0", "\\xA0");
                        logger.Debug(string.Format("CardigannIndexer ({0}): strdump: {1}", ID, DebugData));
                        break;
                    default:
                        break;
                }
            }
            return Data;
        }

        protected IElement QuerySelector(IElement Element, string Selector)
        {
            // AngleSharp doesn't support the :root pseudo selector, so we check for it manually
            if (Selector.StartsWith(":root"))
            {
                Selector = Selector.Substring(5);
                while (Element.ParentElement != null)
                {
                    Element = Element.ParentElement;
                }
            }
            return Element.QuerySelector(Selector);
        }

        protected string handleSelector(selectorBlock Selector, IElement Dom, Dictionary<string, object> variables = null)
        {
            if (Selector.Text != null)
            {
                return applyFilters(applyGoTemplateText(Selector.Text, variables), Selector.Filters, variables);
            }

            IElement selection = Dom;
            string value = null;

            if (Selector.Selector != null)
            {
                if (Dom.Matches(Selector.Selector))
                    selection = Dom;
                else
                    selection = QuerySelector(Dom, Selector.Selector);
                if (selection == null)
                {
                    throw new Exception(string.Format("Selector \"{0}\" didn't match {1}", Selector.Selector, Dom.ToHtmlPretty()));
                }
            }

            if (Selector.Remove != null)
            {
                foreach (var i in selection.QuerySelectorAll(Selector.Remove))
                {
                    i.Remove();
                }
            }

            if (Selector.Case != null)
            {
                foreach (var Case in Selector.Case)
                {
                    if (selection.Matches(Case.Key) || QuerySelector(selection, Case.Key) != null)
                    {
                        value = Case.Value;
                        break;
                    }
                }
                if (value == null)
                    throw new Exception(string.Format("None of the case selectors \"{0}\" matched {1}", string.Join(",", Selector.Case), selection.ToHtmlPretty()));
            }
            else if (Selector.Attribute != null)
            {
                value = selection.GetAttribute(Selector.Attribute);
                if (value == null)
                    throw new Exception(string.Format("Attribute \"{0}\" is not set for element {1}", Selector.Attribute, selection.ToHtmlPretty()));
            }
            else
            {
                value = selection.TextContent;
            }

            return applyFilters(ParseUtil.NormalizeSpace(value), Selector.Filters, variables);
        }

        protected Uri resolvePath(string path, Uri currentUrl = null)
        {
            if (currentUrl == null)
                currentUrl = new Uri(SiteLink);

            return new Uri(currentUrl, path);
        }

        protected override async Task<IEnumerable<ReleaseInfo>> PerformQuery(TorznabQuery query)
        {
            var releases = new List<ReleaseInfo>();

            searchBlock Search = Definition.Search;

            // init template context
            var variables = getTemplateVariablesFromConfigData();

            variables[".Query.Type"] = query.QueryType;
            variables[".Query.Q"] = query.SearchTerm;
            variables[".Query.Series"] = null;
            variables[".Query.Ep"] = query.Episode;
            variables[".Query.Season"] = query.Season;
            variables[".Query.Movie"] = null;
            variables[".Query.Year"] = query.Year.ToString();
            variables[".Query.Limit"] = query.Limit.ToString();
            variables[".Query.Offset"] = query.Offset.ToString();
            variables[".Query.Extended"] = query.Extended.ToString();
            variables[".Query.Categories"] = query.Categories;
            variables[".Query.APIKey"] = query.ApiKey;
            variables[".Query.TVDBID"] = null;
            variables[".Query.TVRageID"] = query.RageID;
            variables[".Query.IMDBID"] = query.ImdbID;
            variables[".Query.IMDBIDShort"] = query.ImdbIDShort;
            variables[".Query.TVMazeID"] = null;
            variables[".Query.TraktID"] = null;
            variables[".Query.Album"] = query.Album;
            variables[".Query.Artist"] = query.Artist;
            variables[".Query.Label"] = query.Label;
            variables[".Query.Track"] = query.Track;
            //variables[".Query.Genre"] = query.Genre ?? new List<string>();
            variables[".Query.Episode"] = query.GetEpisodeSearchString();

            variables[".Query.TrackerCategories"] = query.TrackerCategories ?? new string[0];

            var mappedCategories = MapTorznabCapsToTrackers(query);
            if (mappedCategories.Count == 0)
            {
                mappedCategories = this.DefaultCategories;
            }

            variables[".Categories"] = mappedCategories;

            var KeywordTokens = new List<string>();
            var KeywordTokenKeys = new List<string> { "Q", "Series", "Movie", "Year" };
            foreach (var key in KeywordTokenKeys)
            {
                var Value = (string)variables[".Query." + key];
                if (!string.IsNullOrWhiteSpace(Value))
                    KeywordTokens.Add(Value);
            }

            if (!string.IsNullOrWhiteSpace((string)variables[".Query.Episode"]))
                KeywordTokens.Add((string)variables[".Query.Episode"]);
            variables[".Query.Keywords"] = string.Join(" ", KeywordTokens);
            variables[".Keywords"] = applyFilters((string)variables[".Query.Keywords"], Search.Keywordsfilters);

            // TODO: prepare queries first and then send them parallel 
            // TorentRT hack: do not read multipage data even if config specifies it. It is too slow. I'd rather use paging instead.
            // Take maximum 2 for now.
            var SearchPaths = Search.Paths.Take(2);
            foreach (var SearchPath in SearchPaths)
            {
                // skip path if categories don't match
                if (SearchPath.Categories != null && mappedCategories.Count > 0)
                {
                    var invertMatch = (SearchPath.Categories[0] == "!");
                    var hasIntersect = mappedCategories.Intersect(SearchPath.Categories).Any();
                    if (invertMatch)
                        hasIntersect = !hasIntersect;
                    if (!hasIntersect)
                        continue;
                }

                // build search URL
                // HttpUtility.UrlPathEncode seems to only encode spaces, we use UrlEncode and replace + with %20 as a workaround
                var searchUrl = resolvePath(applyGoTemplateText(SearchPath.Path, variables, WebUtility.UrlEncode).Replace("+", "%20")).AbsoluteUri;
                var queryCollection = new List<KeyValuePair<string, string>>();
                RequestType method = RequestType.GET;

                if (String.Equals(SearchPath.Method, "post", StringComparison.OrdinalIgnoreCase))
                {
                    method = RequestType.POST;
                }

                var InputsList = new List<Dictionary<string, string>>();
                if (SearchPath.Inheritinputs)
                    InputsList.Add(Search.Inputs);
                InputsList.Add(SearchPath.Inputs);

                foreach (var Inputs in InputsList)
                {
                    if (Inputs != null)
                    {
                        foreach (var Input in Inputs)
                        {
                            if (Input.Key == "$raw")
                            {
                                var rawStr = applyGoTemplateText(Input.Value, variables, WebUtility.UrlEncode);
                                foreach (string part in rawStr.Split('&'))
                                {
                                    var parts = part.Split(new char[] { '=' }, 2);
                                    var key = parts[0];
                                    if (key.Length == 0)
                                        continue;
                                    var value = "";
                                    if (parts.Count() == 2)
                                        value = parts[1];
                                    queryCollection.Add(key, value);
                                }
                            }
                            else
                                queryCollection.Add(Input.Key, applyGoTemplateText(Input.Value, variables));
                        }
                    }
                }

                if (method == RequestType.GET)
                {
                    if (queryCollection.Count > 0)
                        searchUrl += (searchUrl.Contains("?") ? "&" : "?") + queryCollection.GetQueryString(Encoding);
                }
                var searchUrlUri = new Uri(searchUrl);

                // send HTTP request
                WebClientStringResult response = null;
                Dictionary<string, string> headers = null;
                if (Search.Headers != null)
                {
                    // FIXME: fix jackett header handling (allow it to specifiy the same header multipe times)
                    headers = new Dictionary<string, string>();
                    foreach (var header in Search.Headers)
                        headers.Add(header.Key, header.Value[0]);
                }
                if (method == RequestType.POST)
                    response = await PostDataWithCookies(searchUrl, queryCollection, null, null, headers);
                else
                    response = await RequestStringWithCookies(searchUrl, null, null, headers);

                if (response.IsRedirect && SearchPath.Followredirect)
                    await FollowIfRedirect(response);

                var results = response.Content;


                try
                {
                    var SearchResultParser = new HtmlParser();
                    var SearchResultDocument = SearchResultParser.ParseDocument(results);

                    // check if we need to login again
                    var loginNeeded = CheckIfLoginIsNeeded(response, SearchResultDocument);
                    if (loginNeeded)
                    {
                        logger.Info(string.Format("CardigannIndexer ({0}): Relogin required", ID));
                        var LoginResult = await DoLogin();
                        if (!LoginResult)
                            throw new Exception(string.Format("Relogin failed"));
                        await TestLogin();
                        if (method == RequestType.POST)
                            response = await PostDataWithCookies(searchUrl, queryCollection);
                        else
                            response = await RequestStringWithCookies(searchUrl);

                        if (response.IsRedirect && SearchPath.Followredirect)
                            await FollowIfRedirect(response);

                        results = response.Content;
                        SearchResultDocument = SearchResultParser.ParseDocument(results);
                    }

                    checkForError(response, Definition.Search.Error);

                    if (Search.Preprocessingfilters != null)
                    {
                        results = applyFilters(results, Search.Preprocessingfilters, variables);
                        SearchResultDocument = SearchResultParser.ParseDocument(results);
                        logger.Debug(string.Format("CardigannIndexer ({0}): result after preprocessingfilters: {1}", ID, results));
                    }

                    var rowsSelector = applyGoTemplateText(Search.Rows.Selector, variables);
                    var RowsDom = SearchResultDocument.QuerySelectorAll(rowsSelector);
                    List<IElement> Rows = new List<IElement>();
                    foreach (var RowDom in RowsDom)
                    {
                        Rows.Add(RowDom);
                    }

                    // merge following rows for After selector
                    var After = Definition.Search.Rows.After;
                    if (After > 0)
                    {
                        for (int i = 0; i < Rows.Count; i += 1)
                        {
                            var CurrentRow = Rows[i];
                            for (int j = 0; j < After; j += 1)
                            {
                                var MergeRowIndex = i + j + 1;
                                var MergeRow = Rows[MergeRowIndex];
                                List<INode> MergeNodes = new List<INode>();
                                foreach (var node in MergeRow.ChildNodes)
                                {
                                    MergeNodes.Add(node);
                                }
                                CurrentRow.Append(MergeNodes.ToArray());
                            }
                            Rows.RemoveRange(i + 1, After);
                        }
                    }

                    foreach (var Row in Rows)
                    {
                        try
                        {
                            var release = new ReleaseInfo();
                            release.MinimumRatio = 1;
                            release.MinimumSeedTime = 48 * 60 * 60;

                            // Parse fields
                            foreach (var Field in Search.Fields)
                            {
                                var FieldParts = Field.Key.Split('|');
                                var FieldName = FieldParts[0];
                                var FieldModifiers = new List<string>();
                                for (var i = 1; i < FieldParts.Length; i++)
                                    FieldModifiers.Add(FieldParts[i]);

                                string value = null;
                                var variablesKey = ".Result." + FieldName;
                                try
                                {
                                    value = handleSelector(Field.Value, Row, variables);
                                    switch (FieldName)
                                    {
                                        case "download":
                                            if (string.IsNullOrEmpty(value))
                                            {
                                                value = null;
                                                release.Link = null;
                                                break;
                                            }
                                            if (value.StartsWith("magnet:"))
                                            {
                                                release.MagnetUri = new Uri(value);
                                                //release.Link = release.MagnetUri;
                                                value = release.MagnetUri.ToString();
                                            }
                                            else
                                            {
                                                release.Link = resolvePath(value, searchUrlUri);
                                                value = release.Link.ToString();
                                            }
                                            break;
                                        case "magnet":
                                            var magnetUri = new Uri(value);
                                            release.MagnetUri = magnetUri;
                                            value = magnetUri.ToString();
                                            if (release.Guid == null)
                                                release.Guid = magnetUri;
                                            break;
                                        case "details":
                                            var url = resolvePath(value, searchUrlUri);
                                            release.Guid = url;
                                            release.Comments = url;
                                            if (release.Guid == null)
                                                release.Guid = url;
                                            value = url.ToString();
                                            break;
                                        case "comments":
                                            var CommentsUrl = resolvePath(value, searchUrlUri);
                                            if (release.Comments == null)
                                                release.Comments = CommentsUrl;
                                            if (release.Guid == null)
                                                release.Guid = CommentsUrl;
                                            value = CommentsUrl.ToString();
                                            break;
                                        case "title":
                                            if (FieldModifiers.Contains("append"))
                                                release.Title += value;
                                            else
                                                release.Title = value;
                                            value = release.Title;
                                            break;
                                        case "description":
                                            if (FieldModifiers.Contains("append"))
                                                release.Description += value;
                                            else
                                                release.Description = value;
                                            value = release.Description;
                                            break;
                                        case "category":
                                            var cats = MapTrackerCatToNewznab(value);
                                            if (release.Category == null)
                                            {
                                                release.Category = cats;
                                            }
                                            else
                                            {
                                                foreach (var cat in cats)
                                                {
                                                    if (!release.Category.Contains(cat))
                                                        release.Category.Add(cat);
                                                }
                                            }
                                            value = release.Category.ToString();
                                            break;
                                        case "size":
                                            release.Size = ReleaseInfo.GetBytes(value);
                                            value = release.Size.ToString();
                                            break;
                                        case "leechers":
                                            var Leechers = ParseUtil.CoerceInt(value);
                                            if (release.Peers == null)
                                                release.Peers = Leechers;
                                            else
                                                release.Peers += Leechers;
                                            value = Leechers.ToString();
                                            break;
                                        case "seeders":
                                            release.Seeders = ParseUtil.CoerceInt(value);
                                            if (release.Peers == null)
                                                release.Peers = release.Seeders;
                                            else
                                                release.Peers += release.Seeders;
                                            value = release.Seeders.ToString();
                                            break;
                                        case "date":
                                            release.PublishDate = DateTimeUtil.FromUnknown(value);
                                            value = release.PublishDate.ToString(DateTimeUtil.RFC1123ZPattern, CultureInfo.InvariantCulture);
                                            break;
                                        case "files":
                                            release.Files = ParseUtil.CoerceLong(value);
                                            value = release.Files.ToString();
                                            break;
                                        case "grabs":
                                            release.Grabs = ParseUtil.CoerceLong(value);
                                            value = release.Grabs.ToString();
                                            break;
                                        case "downloadvolumefactor":
                                            release.DownloadVolumeFactor = ParseUtil.CoerceDouble(value);
                                            value = release.DownloadVolumeFactor.ToString();
                                            break;
                                        case "uploadvolumefactor":
                                            release.UploadVolumeFactor = ParseUtil.CoerceDouble(value);
                                            value = release.UploadVolumeFactor.ToString();
                                            break;
                                        case "minimumratio":
                                            release.MinimumRatio = ParseUtil.CoerceDouble(value);
                                            value = release.MinimumRatio.ToString();
                                            break;
                                        case "minimumseedtime":
                                            release.MinimumSeedTime = ParseUtil.CoerceLong(value);
                                            value = release.MinimumSeedTime.ToString();
                                            break;
                                        case "imdb":
                                            release.Imdb = ParseUtil.GetLongFromString(value);
                                            value = release.Imdb.ToString();
                                            break;
                                        case "rageid":
                                            Regex RageIDRegEx = new Regex(@"(\d+)", RegexOptions.Compiled);
                                            var RageIDMatch = RageIDRegEx.Match(value);
                                            var RageID = RageIDMatch.Groups[1].Value;
                                            release.RageID = ParseUtil.CoerceLong(RageID);
                                            value = release.RageID.ToString();
                                            break;
                                        case "tvdbid":
                                            Regex TVDBIdRegEx = new Regex(@"(\d+)", RegexOptions.Compiled);
                                            var TVDBIdMatch = TVDBIdRegEx.Match(value);
                                            var TVDBId = TVDBIdMatch.Groups[1].Value;
                                            release.TVDBId = ParseUtil.CoerceLong(TVDBId);
                                            value = release.TVDBId.ToString();
                                            break;
                                        case "banner":
                                            if (!string.IsNullOrWhiteSpace(value))
                                            {
                                                var bannerurl = resolvePath(value, searchUrlUri);
                                                release.BannerUrl = bannerurl;
                                            }
                                            value = release.BannerUrl.ToString();
                                            break;
                                        default:
                                            break;
                                    }
                                    variables[variablesKey] = value;
                                }
                                catch (Exception ex)
                                {
                                    if (!variables.ContainsKey(variablesKey))
                                        variables[variablesKey] = null;
                                    if (OptionalFileds.Contains(Field.Key) || FieldModifiers.Contains("optional") || Field.Value.Optional)
                                        continue;
                                    throw new Exception(string.Format("Error while parsing field={0}, selector={1}, value={2}: {3}", Field.Key, Field.Value.Selector, (value == null ? "<null>" : value), ex.Message));
                                }
                            }

                            var Filters = Definition.Search.Rows.Filters;
                            var SkipRelease = false;
                            if (Filters != null)
                            {
                                foreach (filterBlock Filter in Filters)
                                {
                                    switch (Filter.Name)
                                    {
                                        case "andmatch":
                                            int CharacterLimit = -1;
                                            if (Filter.Args != null)
                                                CharacterLimit = int.Parse(Filter.Args);

                                            if (query.ImdbID != null && TorznabCaps.SupportsImdbMovieSearch)
                                                break; // skip andmatch filter for imdb searches

                                            if (!query.MatchQueryStringAND(release.Title, CharacterLimit))
                                            {
                                                logger.Debug(string.Format("CardigannIndexer ({0}): skipping {1} (andmatch filter)", ID, release.Title));
                                                SkipRelease = true;
                                            }
                                            break;
                                        case "strdump":
                                            // for debugging
                                            logger.Debug(string.Format("CardigannIndexer ({0}): row strdump: {1}", ID, Row.ToHtmlPretty()));
                                            break;
                                        default:
                                            logger.Error(string.Format("CardigannIndexer ({0}): Unsupported rows filter: {1}", ID, Filter.Name));
                                            break;
                                    }
                                }
                            }

                            if (SkipRelease)
                                continue;

                            // if DateHeaders is set go through the previous rows and look for the header selector
                            var DateHeaders = Definition.Search.Rows.Dateheaders;
                            if (release.PublishDate == DateTime.MinValue && DateHeaders != null)
                            {
                                var PrevRow = Row.PreviousElementSibling;
                                string value = null;
                                if (PrevRow == null) // continue with parent
                                {
                                    var Parent = Row.ParentElement;
                                    if (Parent != null)
                                        PrevRow = Parent.PreviousElementSibling;
                                }
                                while (PrevRow != null)
                                {
                                    var CurRow = PrevRow;
                                    logger.Debug(PrevRow.OuterHtml);
                                    try
                                    {
                                        value = handleSelector(DateHeaders, CurRow);
                                        break;
                                    }
                                    catch (Exception)
                                    {
                                        // do nothing
                                    }
                                    PrevRow = CurRow.PreviousElementSibling;
                                    if (PrevRow == null) // continue with parent
                                    {
                                        var Parent = CurRow.ParentElement;
                                        if (Parent != null)
                                            PrevRow = Parent.PreviousElementSibling;
                                    }
                                }

                                if (value == null && DateHeaders.Optional == false)
                                    throw new Exception(string.Format("No date header row found for {0}", release.ToString()));
                                if (value != null)
                                    release.PublishDate = DateTimeUtil.FromUnknown(value);
                            }

                            releases.Add(release);
                        }
                        catch (Exception ex)
                        {
                            logger.Error(string.Format("CardigannIndexer ({0}): Error while parsing row '{1}':\n\n{2}", ID, Row.ToHtmlPretty(), ex));
                        }
                    }
                }
                catch (Exception ex)
                {
                    OnParseError(results, ex);
                }
            }
            if (query.Limit > 0)
                releases = releases.Take(query.Limit).ToList();
            return releases;
        }

        protected async Task<WebClientByteResult> handleRequest(requestBlock request, Dictionary<string, object> variables = null, string referer = null)
        {
            var requestLinkStr = resolvePath(applyGoTemplateText(request.Path, variables)).ToString();

            Dictionary<string, string> pairs = null;
            var queryCollection = new NameValueCollection();

            RequestType method = RequestType.GET;
            if (String.Equals(request.Method, "post", StringComparison.OrdinalIgnoreCase))
            {
                method = RequestType.POST;
                pairs = new Dictionary<string, string>();
            }

            foreach (var Input in request.Inputs)
            {
                var value = applyGoTemplateText(Input.Value, variables);
                if (method == RequestType.GET)
                    queryCollection.Add(Input.Key, value);
                else if (method == RequestType.POST)
                    pairs.Add(Input.Key, value);
            }

            if (queryCollection.Count > 0)
            {
                if (!requestLinkStr.Contains("?"))
                    requestLinkStr += "?" + queryCollection.GetQueryString(Encoding).Substring(1);
                else
                    requestLinkStr += queryCollection.GetQueryString(Encoding);
            }

            var response = await RequestBytesWithCookiesAndRetry(requestLinkStr, null, method, referer, pairs);
            logger.Debug($"CardigannIndexer ({ID}): handleRequest() remote server returned {response.Status.ToString()}" + (response.IsRedirect ? " => " + response.RedirectingTo : ""));
            return response;
        }

        protected IDictionary<string, object> AddTemplateVariablesFromUri(IDictionary<string, object> variables, Uri uri, string prefix = "")
        {
            variables[prefix + ".AbsoluteUri"] = uri.AbsoluteUri;
            variables[prefix + ".AbsolutePath"] = uri.AbsolutePath;
            variables[prefix + ".Scheme"] = uri.Scheme;
            variables[prefix + ".Host"] = uri.Host;
            variables[prefix + ".Port"] = uri.Port.ToString();
            variables[prefix + ".PathAndQuery"] = uri.PathAndQuery;
            variables[prefix + ".Query"] = uri.Query;
            //var queryString = QueryHelpers.ParseQuery(uri.Query);
            //foreach (string key in queryString.Keys)
            //{
            //    //If we have supplied the same query string multiple time, just take the first.
            //    variables[prefix + ".Query." + key] = queryString[key].First();
            //}
            var queryString = HttpUtility.ParseQueryString(uri.Query);
            foreach (string key in queryString.Keys)
            {
                //If we have supplied the same query string multiple time, just take the first.
                variables[prefix + ".Query." + key] = queryString[key];
            }
            return variables;
        }

        public override async Task<byte[]> Download(Uri link)
        {
            var method = RequestType.GET;
            if (Definition.Download != null)
            {
                var Download = Definition.Download;
                var variables = getTemplateVariablesFromConfigData();
                AddTemplateVariablesFromUri(variables, link, ".DownloadUri");
                if (Download.Before != null)
                {
                    var beforeresult = await handleRequest(Download.Before, variables, link.ToString());
                }
                if (Download.Method != null)
                {
                    if (Download.Method == "post")
                        method = RequestType.POST;
                }
                if (Download.Selector != null)
                {
                    var selector = applyGoTemplateText(Download.Selector, variables);
                    var response = await RequestStringWithCookies(link.ToString());
                    if (response.IsRedirect)
                        response = await RequestStringWithCookies(response.RedirectingTo);
                    var results = response.Content;
                    var SearchResultParser = new HtmlParser();
                    var SearchResultDocument = SearchResultParser.ParseDocument(results);
                    var DlUri = SearchResultDocument.QuerySelector(selector);
                    if (DlUri != null)
                    {
                        logger.Debug(string.Format("CardigannIndexer ({0}): Download selector {1} matched:{2}", ID, selector, DlUri.ToHtmlPretty()));
                        var href = DlUri.GetAttribute("href");
                        href = applyFilters(href, Download.Filters, variables);
                        link = resolvePath(href, link);
                    }
                    else
                    {
                        logger.Error(string.Format("CardigannIndexer ({0}): Download selector {1} didn't match:\n{2}", ID, Download.Selector, results));
                        throw new Exception(string.Format("Download selector {0} didn't match", Download.Selector));
                    }
                }
            }
            return await base.Download(link, method);
        }
    }
}

