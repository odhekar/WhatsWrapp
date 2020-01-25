using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Template10.Mvvm;
using Template10.Services.NavigationService;
using Template10.Services.SettingsService;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Resources;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;
using Windows.Web.Http;

namespace WhatsWrapper.ViewModels
{
    public class MainPageViewModel : ViewModelBase
    {
        private WebView whatsAppWebView;
        private ResourceLoader loader = new ResourceLoader();

        //Color: #2CB53F
        private string WhatsAppUrl
        {
            get
            {
                return "web.whatsapp.com";
                //return "www.whatismybrowser.com/detect/what-http-headers-is-my-browser-sending";
            }
        }

        private int notificationsCount { get; set; }

        public Dictionary<string, string> ChatsAndCounts { get; set; }

        public string Version
        {
            get
            {
                PackageVersion version = Package.Current.Id.Version;
                return string.Format("{0}.{1}.{2}", version.Major, version.Minor, version.Build);
            }
        }

        private bool showNotifications;

        public bool ShowNotifications
        {
            get
            {
                return showNotifications;
            }

            set
            {
                Set(ref showNotifications, value);
                SettingsService.Roaming.Write(nameof(ShowNotifications), showNotifications);
            }
        }

        public bool IsWindowInFocus { get; set; }

        private DelegateCommand reviewAppCommand;

        public DelegateCommand ReviewAppCommand
        {
            get
            {
                if (reviewAppCommand == null)
                {
                    reviewAppCommand = new DelegateCommand(async () =>
                    {
                        string storeLink = "ms-windows-store:REVIEW?PFN=" + Package.Current.Id.FamilyName;
                        await Windows.System.Launcher.LaunchUriAsync(new Uri(storeLink));
                    });
                }
                return reviewAppCommand;
            }
        }


        #region Page Events
        public override Task OnNavigatedToAsync(object parameter, NavigationMode mode, IDictionary<string, object> state)
        {
            Debug.WriteLine("navigated");
            ShowNotifications = SettingsService.Roaming.Read(nameof(ShowNotifications), true);
            ChatsAndCounts = new Dictionary<string, string>();

            Windows.UI.Xaml.Window.Current.Activated += (sender, eArgs) =>
            {
                switch (eArgs.WindowActivationState)
                {
                    case Windows.UI.Core.CoreWindowActivationState.CodeActivated:
                    case Windows.UI.Core.CoreWindowActivationState.PointerActivated:
                        IsWindowInFocus = true;
                        break;
                    case Windows.UI.Core.CoreWindowActivationState.Deactivated:
                        IsWindowInFocus = false;
                        break;
                }
            };

            return Task.CompletedTask;
        }
        #endregion

        #region Control Events
        public void WhatsAppWebView_PermissionRequested(WebView sender, WebViewPermissionRequestedEventArgs args)
        {
            if (args.PermissionRequest.PermissionType == WebViewPermissionType.Media
                && args.PermissionRequest.Uri.Host == WhatsAppUrl)
            {
                args.PermissionRequest.Allow();
            }
        }

        public void WhatsAppWebView_Loaded(object sender, RoutedEventArgs e)
        {
            Debug.WriteLine("Loaded");
            whatsAppWebView = sender as WebView;

            HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Get, new Uri("http://" + WhatsAppUrl));
            req.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; WOW64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/50.0.2661.102 Safari/537.36");
            req.Headers.Add("Accept-Language", Windows.System.UserProfile.GlobalizationPreferences.Languages[0]);
            whatsAppWebView.NavigateWithHttpRequestMessage(req);
            whatsAppWebView.RegisterPropertyChangedCallback(WebView.DocumentTitleProperty, OnDocumentTitleChanged);
        }

        private async void OnDocumentTitleChanged(DependencyObject sender, DependencyProperty dp)
        {
            Debug.WriteLine("Doc title: " + whatsAppWebView.DocumentTitle);
            if (ShowNotifications)
            {
                await Notify();
            }
        }

        #endregion

        #region Helper methods
        private async Task Notify()
        {
            if (whatsAppWebView.DocumentTitle.Trim().Length > 0)
            {
                string countStr = Regex.Replace(whatsAppWebView.DocumentTitle, "[^0-9]", "");
                int newChatCount = 0;
                int.TryParse(countStr, out newChatCount);
                if (newChatCount != notificationsCount && newChatCount > 0)
                {
                    notificationsCount = newChatCount;
                    var chatsAndCounts = await GetChatsAndCounts();
                    try
                    {
                        if (!IsWindowInFocus)
                        {
                            ShowToastNotification(newChatCount, chatsAndCounts);
                        }
                        ShowTileNotification(newChatCount, chatsAndCounts);
                    }
                    catch
                    {
                        Debug.WriteLine("Exception occurred in show toast or tile. Skipping notification.");
                    }
                }
            }
        }

        private async Task<string> EvalJSBlock(string jsBlock)
        {
            string[] args = { jsBlock };
            string result = await whatsAppWebView.InvokeScriptAsync("eval", args);
            return result;
        }

        private async Task<string> GetAllChatCount()
        {
            string jsBlock = @"(function(){var ele=document.getElementsByClassName('unread-count');var count=0;
                            for(var i=0;i<ele.length;i++){count+=parseInt(ele[i].textContent);}return count.toString();})();";
            return await EvalJSBlock(jsBlock);
        }

        private async Task<Dictionary<string, string>> GetChatsAndCounts()
        {
            var newChatsAndCounts = new Dictionary<string, string>();

            string jsBlock = @"(function(){var csv=[];var chats=document.querySelectorAll('div.chatlist div.unread.chat');
                            for(var i=0;i<chats.length;i++){var title=chats[i].querySelector('div.chat-title span').getAttribute('title');csv.push(title);
                            var count=chats[i].querySelector('div.chat-secondary span.unread-count').textContent;csv.push(count);}return csv.join(',');})();";
            string csv = await EvalJSBlock(jsBlock);
            Debug.WriteLine(csv);
            string[] chatsArray = csv.Split(',');
            //If array length is odd, then something is wrong. Only execute remaining block if array has even number of entries
            if (chatsArray.Length % 2 == 0)
            {
                int i = 0;
                while (i < chatsArray.Length)
                {
                    string key = chatsArray[i].Trim();
                    string value = chatsArray[i + 1].Trim();
                    //check if chat count has changed
                    if (ChatsAndCounts.ContainsKey(key))
                    {
                        if (!ChatsAndCounts[key].Equals(value))
                        {
                            //First update existing chat count in ChatsAndCounts
                            ChatsAndCounts[key] = value;
                            //Then collect new counts for notification
                            newChatsAndCounts.Add(key, value);
                        }
                    }
                    else
                    {
                        ChatsAndCounts.Add(key, value);
                        newChatsAndCounts.Add(key, value);
                    }
                    i += 2;
                }
            }

            return newChatsAndCounts;
        }

        private async Task<string> GetUserAvatar()
        {
            //string[] args = { "(function(){var ele=document.querySelector('header.pane-header div.icon-user-default img');try{return ele.getAttribute('src');}catch(err){return 'error';}})();" };
            string jsBlock = "(function(){var ele=document.querySelector('div.chatlist div.unread div.avatar img');try{return ele.getAttribute('class') + '<$$$$>' + ele.getAttribute('src');}catch(err){return 'error';}})();";
            string avatarUrl = await EvalJSBlock(jsBlock);
            Debug.WriteLine(avatarUrl);
            return avatarUrl;
        }

        private void ShowToast(string xmlToastTemplate)
        {
            Debug.WriteLine(xmlToastTemplate);
            var xmlDocument = new XmlDocument();
            XmlLoadSettings xmlRules = new XmlLoadSettings();
            xmlRules.ValidateOnParse = true;
            xmlDocument.LoadXml(xmlToastTemplate, xmlRules);
            var toastNotification = new ToastNotification(xmlDocument);
            ToastNotificationManager.CreateToastNotifier().Show(toastNotification);
        }

        private void ShowToastNotification(int allChatCount, Dictionary<string, string> newChatsAndCounts)
        {
            string secondLine = "<text>";
            foreach (KeyValuePair<string, string> kvp in newChatsAndCounts)
            {
                secondLine += kvp.Key + ": " + kvp.Value + ", ";

            }
            secondLine = secondLine.Substring(0, secondLine.Length - 2);
            secondLine += "</text>";

            var xmlToastTemplate = @"<toast launch='app-defined-string'>
                                        <visual>
                                            <binding template ='ToastGeneric'>"
                                                + "<text>" + string.Format(loader.GetString("ToastTitle"), allChatCount) + "</text>"
                                                + secondLine
                                        + @"</binding>
                                        </visual>
                                    </toast>";

            Debug.WriteLine(xmlToastTemplate);
            xmlToastTemplate = xmlToastTemplate.Replace("&", "");
            Debug.WriteLine(xmlToastTemplate);
            ShowToast(xmlToastTemplate);
        }

        private void ShowTile(string xmlTileTemplate)
        {
            var xmlDocument = new XmlDocument();
            XmlLoadSettings xmlRules = new XmlLoadSettings();
            xmlRules.ValidateOnParse = true;
            xmlDocument.LoadXml(xmlTileTemplate, xmlRules);
            var tileNotification = new TileNotification(xmlDocument);
            tileNotification.ExpirationTime = DateTime.Now.AddMinutes(5);
            TileUpdateManager.CreateTileUpdaterForApplication().Clear();
            TileUpdateManager.CreateTileUpdaterForApplication().Update(tileNotification);
        }

        private void ShowTileNotification(int allChatCount, Dictionary<string, string> newChatsAndCounts)
        {
            string medium = "";
            string wideAndLarge = "";
            foreach (KeyValuePair<string, string> kvp in newChatsAndCounts)
            {
                var truncatedKey = kvp.Key.Length > 15 ? kvp.Key.Substring(0, 15) : kvp.Key;
                medium += "<text>" + truncatedKey + ": " + kvp.Value + "</text>";
                wideAndLarge += "<text>" + truncatedKey + ": " + kvp.Value + " " + loader.GetString("TileNewMessages") + "</text>";
            }

            var xmlTileTemplate = @"<tile>
                                        <visual>
                                            <binding template='TileMedium' hint-textStacking='center' branding='Logo'>"
                                            + medium
                                            + @"</binding>
                                            <binding template='TileWide' branding='NameAndLogo'>"
                                            + wideAndLarge
                                            + @"</binding>
                                            <binding template='TileLarge' branding='NameAndLogo'>"
                                            + wideAndLarge
                                            + @"</binding>
                                        </visual>
                                    </tile>";
            Debug.WriteLine(xmlTileTemplate);
            xmlTileTemplate = xmlTileTemplate.Replace("&", "");
            Debug.WriteLine(xmlTileTemplate);
            ShowTile(xmlTileTemplate);
        }

        #endregion

    }
}

