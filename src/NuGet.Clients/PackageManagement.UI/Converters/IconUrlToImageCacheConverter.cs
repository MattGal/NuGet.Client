﻿using System;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Runtime.Caching;
using System.Windows.Data;
using System.Windows.Media.Imaging;

namespace NuGet.PackageManagement.UI
{
    internal class IconUrlToImageCacheConverter : IValueConverter
    {
        // same URIs can reuse the bitmapImage that we've already used.
        private static readonly ObjectCache _bitmapImageCache = System.Runtime.Caching.MemoryCache.Default;

        private readonly WebExceptionStatus[] FatalErrors = new[]
        {
            WebExceptionStatus.ConnectFailure,
            WebExceptionStatus.RequestCanceled,
            WebExceptionStatus.ConnectionClosed,
            WebExceptionStatus.Timeout,
            WebExceptionStatus.UnknownError
        };

        // If we fail at least this high (failures/attempts), we'll shut off image loads.
        // TODO: Should we allow this to be overridden in nuget.config.
        private const double StopLoadingImageThreshold = 0.50;

        private static readonly System.Net.Cache.RequestCachePolicy RequestCacheIfAvailable = new System.Net.Cache.RequestCachePolicy(System.Net.Cache.RequestCacheLevel.CacheIfAvailable);

        private static long _iconLoadAttempts = 0;
        private static int _iconFatalFailures = 0;

        // We bind to a BitmapImage instead of a Uri so that we can control the decode size, since we are displaying 32x32 images, while many of the images are 128x128 or larger.
        // This leads to a memory savings.
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var iconUrl = value as Uri;
            var defaultPackageIcon = parameter as BitmapImage;
            if (iconUrl == null)
            {
                return null;
            }

            var iconBitmapImage = _bitmapImageCache.Get(iconUrl.ToString()) as BitmapImage;
            if (iconBitmapImage != null)
            {
                return iconBitmapImage;
            }

            // Some people run on networks with internal NuGet feeds, but no access to the package images on the internet.
            // This is meant to detect that kind of case, and stop spamming the network, so the app remains responsive.
            if (_iconFatalFailures > 5 && ((double)_iconFatalFailures / _iconLoadAttempts) > StopLoadingImageThreshold)
            {
                return null;
            }

            iconBitmapImage = new BitmapImage();
            iconBitmapImage.BeginInit();
            iconBitmapImage.UriSource = iconUrl;

            // Default cache policy: Per MSDN, satisfies a request for a resource either by using the cached copy of the resource or by sending a request
            // for the resource to the server. The action taken is determined by the current cache policy and the age of the content in the cache.
            // This is the cache level that should be used by most applications.
            iconBitmapImage.UriCachePolicy = RequestCacheIfAvailable;

            // Instead of scaling larger images and keeping larger image in memory, this makes it so we scale it down, and throw away the bigger image.
            // Only need to set this on one dimension, to preserve aspect ratio
            iconBitmapImage.DecodePixelWidth = 32;

            iconBitmapImage.DecodeFailed += IconBitmapImage_DownloadOrDecodeFailed;
            iconBitmapImage.DownloadFailed += IconBitmapImage_DownloadOrDecodeFailed;
            iconBitmapImage.DownloadCompleted += IconBitmapImage_DownloadCompleted;

            try
            {
                iconBitmapImage.EndInit();
            }
            // if the URL is a file: URI (which actually happened!), we'll get an exception.
            // if the URL is a file: URI which is in an existing directory, but the file doesn't exist, we'll fail silently.
            catch (Exception e) when (e is System.IO.IOException || e is System.Net.WebException)
            {
                iconBitmapImage = defaultPackageIcon;
            }
            finally
            {
                // store this bitmapImage in the bitmap image cache, so that other occurances can reuse the BitmapImage
                AddToCache(iconUrl, iconBitmapImage);

                // if we hit maxValue, reset both failures and loadattempts.
                if (int.MaxValue > _iconLoadAttempts)
                {
                    _iconLoadAttempts++;
                }
                else
                {
                    _iconLoadAttempts = 0;
                    _iconFatalFailures = 0;
                }
            }

            return iconBitmapImage;
        }

        private static void AddToCache(Uri iconUrl, BitmapImage iconBitmapImage)
        {
            var policy = new CacheItemPolicy
            {
                AbsoluteExpiration = DateTimeOffset.Now.AddMinutes(10)
            };
            _bitmapImageCache.Set(iconUrl.ToString(), iconBitmapImage, policy);
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }

        private void IconBitmapImage_DownloadCompleted(object sender, EventArgs e)
        {
            var bitmapImage = sender as BitmapImage;
            if (!bitmapImage.IsFrozen)
            {
                bitmapImage.Freeze();
            }
        }

        private void IconBitmapImage_DownloadOrDecodeFailed(object sender, System.Windows.Media.ExceptionEventArgs e)
        {
            var bitmapImage = sender as BitmapImage;

            // Fix the bitmap image cache to have default package icon, if some other failure didn't already do that.
            var iconBitmapImage = _bitmapImageCache.Get(bitmapImage.UriSource.ToString()) as BitmapImage;
            if (iconBitmapImage != Images.DefaultPackageIcon)
            {
                AddToCache(bitmapImage.UriSource, Images.DefaultPackageIcon);

                var webex = e.ErrorException as WebException;
                if (webex != null && FatalErrors.Any(c => webex.Status == c))
                {
                    _iconFatalFailures++;
                }
            }
        }
    }
}
