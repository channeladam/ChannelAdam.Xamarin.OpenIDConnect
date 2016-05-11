//-----------------------------------------------------------------------
// <copyright file="OpenIDConnectWebView.cs">
//     Copyright (c) 2016 Adam Craven. All rights reserved.
// </copyright>
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//    http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.
//-----------------------------------------------------------------------
using IdentityModel.OidcClient.WebView;
using System;
using System.Threading;
using System.Threading.Tasks;
using Xamarin.Forms;

namespace ChannelAdam.Xamarin.OpenIDConnect
{
    /// <summary>
    /// An implementation of the <see cref="IWebView"/> using Xamarin's WebView browser.
    /// </summary>
    public class OpenIDConnectWebView : IWebView
    {
        private readonly INavigation _nav;
        private Func<ContentPage> _pageFactory;
        private object _callbackFlowCompleteLock = new object();
        private bool _isCallbackFlowComplete;
        private InvokeResult _invokeResult;
        private SemaphoreSlim _flowCompleteSignal;

        public event EventHandler<HiddenModeFailedEventArgs> HiddenModeFailed;

        public OpenIDConnectWebView(INavigation nav, Func<ContentPage> pageFactory) //, Xamarin.Forms.Element parentElement)
        {
            _nav = nav;
            _pageFactory = pageFactory;
        }

        public OpenIDConnectWebView(INavigation nav) : this(nav, () => new ContentPage())
        { }

        public async Task<InvokeResult> InvokeAsync(InvokeOptions options)
        {
            _isCallbackFlowComplete = false;

            _flowCompleteSignal = new SemaphoreSlim(0, 1);

            _invokeResult = new InvokeResult
            {
                ResultType = InvokeResultType.UserCancel
            };

            ContentPage page = CreatePage();
            WebView webView = CreateWebView(options);
            page.Content = webView;

            StartFlow(options, page, webView);

            await _flowCompleteSignal.WaitAsync();

            HideModal();

            return _invokeResult;
        }

        private void StartFlow(InvokeOptions options, ContentPage page, WebView webView)
        {
            webView.Source = options.StartUrl;

            if (options.InitialDisplayMode != DisplayMode.Visible)
            {
                _invokeResult.ResultType = InvokeResultType.Timeout;

                Device.StartTimer(options.InvisibleModeTimeout, () =>
                {
                    var args = new HiddenModeFailedEventArgs(_invokeResult);
                    HiddenModeFailed?.Invoke(this, args);
                    if (args.Cancel)
                    {
                        webView.Source = string.Empty;
                        HideModal();
                    }
                    else
                    {
                        ShowModal(page);
                    }

                    return false;
                });
            }
            else
            {
                ShowModal(page);
            }
        }

        private ContentPage CreatePage()
        {
            var page = _pageFactory.Invoke();

            page.Disappearing += (sender, eventArgs) =>
            {
                _flowCompleteSignal.Release();
            };
            return page;
        }

        private WebView CreateWebView(InvokeOptions options)
        {
            var webView = new WebView
            {
                VerticalOptions = LayoutOptions.FillAndExpand
            };

            webView.Navigated += WebView_Navigated;
            webView.Navigating += (sender, webNavigatingEventArgs) =>
            {
                HandleAuthRedirect(options, webNavigatingEventArgs);
            };

            return webView;
        }

        private void HandleAuthRedirect(InvokeOptions options, WebNavigatingEventArgs e)
        {
            if (e.Url.StartsWith(options.EndUrl, StringComparison.OrdinalIgnoreCase))
            {
                lock (_callbackFlowCompleteLock)
                {
                    _isCallbackFlowComplete = true;
                }
                e.Cancel = true;
                _invokeResult.ResultType = InvokeResultType.Success;

                if (options.ResponseMode == ResponseMode.FormPost)
                {
                    // TODO: test if this actually works...:
                    var source = e.Source as HtmlWebViewSource;
                    if (source == null)
                    {
                        throw new NotSupportedException("ResponseMode.FormPost is not supported.");
                    }
                    _invokeResult.Response = source.Html;
                }
                else
                {
                    _invokeResult.Response = e.Url;
                }

                _flowCompleteSignal.Release();
            }
        }

        private void WebView_Navigated(object sender, WebNavigatedEventArgs e)
        {
            lock (_callbackFlowCompleteLock)
            {
                if (_isCallbackFlowComplete)
                {
                    return;
                }
            }

            if (e.Result == WebNavigationResult.Failure)
            {
                _invokeResult.ResultType = InvokeResultType.HttpError;
                _invokeResult.Error = "error";
                _flowCompleteSignal.Release();
            }
            else if (e.Result == WebNavigationResult.Timeout)
            {
                _invokeResult.ResultType = InvokeResultType.Timeout;
                _invokeResult.Error = "timeout";
                _flowCompleteSignal.Release();
            }
        }

        private void ShowModal(ContentPage page)
        {
            _nav.PushModalAsync(page).Wait();
        }

        private void HideModal()
        {
            _nav.PopModalAsync();
        }
    }
}