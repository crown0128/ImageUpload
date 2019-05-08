﻿using Google.Apis.PhotosLibrary.v1;
using Google.Apis.PhotosLibrary.v1.Data;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using ExifLibrary;
using google_photos_upload.Extensions;
using Microsoft.Extensions.Logging;

namespace google_photos_upload.Model
{
    class MyImage
    {
        private const string PhotosLibraryPasebath = "https://photoslibrary.googleapis.com/v1/uploads";
        private readonly ILogger _logger = null;
        private readonly PhotosLibraryService service = null;

        private readonly FileInfo mediaFile = null;
        private string uploadToken = null;

        //Get from config file if we should upload img without EXIF
        private readonly bool conf_IMG_UPLOAD_NO_EXIF = Convert.ToBoolean(System.Configuration.ConfigurationManager.AppSettings["IMG_UPLOAD_NO_EXIF"]);



        /// <summary>
        /// Supported file formats
        /// </summary>
        private static readonly string[] allowedMovieFormats = { "mov", "avi" };
        private static readonly string[] allowedPhotoFormats = { "jpg", "jpeg", "gif" };
        private static readonly string[] ignoreFiletypes = { "txt", "thm" };



        public MyImage(ILogger logger, PhotosLibraryService photoService, FileInfo imgFile)
        {
            this._logger = logger;
            this.service = photoService;
            this.mediaFile = imgFile;
            this.UploadStatus = UploadStatus.NotStarted;
            this.MediaType = GetMediaType();
        }

        public MediaTypeEnum MediaType { get; private set; }


        public UploadStatus UploadStatus
        {
            get;
            set;
        }

        /// <summary>
        /// Photo File Name
        /// </summary>
        public string Name
        {
            get { return mediaFile.Name; }
        }

        /// <summary>
        /// Image name in ASCII format
        /// </summary>
        private string NameASCII
        {
            get { return Name.UnicodeToASCII(); }
        }

        /// <summary>
        /// Google Photos UploadToken for the file when it has been uploaded
        /// </summary>
        public string UploadToken
        {
            get { return this.uploadToken; }
        }

        public bool IsPhoto
        {
            get {
                return MediaType == MediaTypeEnum.Photo;
            }
        }


        public bool IsMovie
        {
            get
            {
                return MediaType == MediaTypeEnum.Movie;
            }
        }


        /// <summary>
        /// Gets the 'Date Taken Original' value in DateTime format, derived from image EXIF data (avoids loading the whole image file into memory).
        /// </summary>
        private DateTime? DateImageWasTaken
        {
            get
            {
                try
                {
                    ImageFile imageFile = ImageFile.FromFile(mediaFile.FullName);
                    var datetimeOriginal = imageFile.Properties.FirstOrDefault<ExifProperty>(x => x.Tag == ExifTag.DateTimeOriginal).Value;
                    string datetimeOriginaltxt = datetimeOriginal.ToString();

                    if (DateTime.TryParse(datetimeOriginaltxt, out DateTime dtOriginal))
                        return dtOriginal;

                    //Unable to get date
                    return null;
                }
                catch (Exception)
                {
                    _logger.LogWarning("Failed reading EXIF data...");
                }

                //Unable to extract EXIF data from file
                return null;
            }
        }


        /// <summary>
        /// Get the media type for the file set on the mediaFile class variable.
        /// </summary>
        /// <returns>Media Type</returns>
        private MediaTypeEnum GetMediaType()
        {
            string filename = mediaFile.Name.ToLower();
            string fileext = Path.GetExtension(filename).ToLower();

            //Is Movie?
            if (ignoreFiletypes.Any(fileext.Contains))
            {
                return MediaTypeEnum.Ignore;
            }
            else if (allowedMovieFormats.Any(fileext.Contains))
            {
                return MediaTypeEnum.Movie;
            }
            else if (allowedPhotoFormats.Any(fileext.Contains))
            {
                return MediaTypeEnum.Photo;
            }

            return MediaTypeEnum.Unknown;
        }


        public bool IsFormatSupported
        {
            get {
                try
                {
                    if (IsPhoto)
                    {
                        // EXIF property "Date Taken Original" must be set on the image file to ensure correct date in Google Photos
                        var datetimeOriginal = DateImageWasTaken;

                        if (datetimeOriginal == null)
                        {
                            if (!conf_IMG_UPLOAD_NO_EXIF)
                                return false;

                            _logger.LogWarning("Image will appear with today's date in Google Photos as EXIF data is missing");
                        }

                        
                    }
                    else if (IsMovie)
                    {
                        //Do check or Movie
                    }
                    else
                    {
                        //The file is not a supported Photo or Movie format
                        _logger.LogWarning("The file type is not supported");
                        return false;
                    }

                }
                catch (Exception)
                {
                    //What to do?
                    return false;
                }

                return true;
            }
        }

        /// <summary>
        /// Perform upload of Photo file to Google Cloud
        /// </summary>
        /// <returns></returns>
        public bool UploadMedia()
        {
            UploadStatus = UploadStatus.UploadInProgress;

            this.uploadToken = UploadMediaFile(service);

            if (uploadToken is null)
            {
                UploadStatus = UploadStatus.UploadNotSuccessfull;
                return false;
            }

            //UploadStatus will be set to 'UploadSuccess' later after it has been added to Album

            return true;
        }


        /// <summary>
        /// Upload photo to Google Photos.
        /// API guide for media upload:
        /// https://developers.google.com/photos/library/guides/upload-media
        /// 
        /// The implementation is inspired by this post due to limitations in the Google Photos API missing a method for content upload,
        /// but instead of System.Net.HttpClient it has been rewritten to use Google Photo's HTTP Client as it the  has the Bearer Token already handled.
        /// https://stackoverflow.com/questions/51576778/upload-photos-to-google-photos-api-error-500
        /// </summary>
        /// <param name="photo"></param>
        /// <returns></returns>
        private string UploadMediaFile(PhotosLibraryService photoService)
        {
            string newUploadToken = null;

            try
            {
                using (var fileStream = mediaFile.OpenRead())
                {
                    //Create byte array to store the image for transfer
                    byte[] pixels = new byte[fileStream.Length];

                    //Read image into the pixels byte array
                    fileStream.Read(pixels, 0, (int)fileStream.Length);

                    var httpContent = new ByteArrayContent(pixels);
                    httpContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                    httpContent.Headers.Add("X-Goog-Upload-File-Name", NameASCII);
                    httpContent.Headers.Add("X-Goog-Upload-Protocol", "raw");


                    HttpResponseMessage mediaResponse = photoService.HttpClient.PostAsync(PhotosLibraryPasebath, httpContent).Result;

                    if (mediaResponse.IsSuccessStatusCode)
                        newUploadToken = mediaResponse.Content.ReadAsStringAsync().Result;
                }

            }
            catch (Exception e)
            {
                _logger.LogError(e, "Upload of Image stream failed");
                throw;
            }

            photoService.HttpClient.DefaultRequestHeaders.Clear();

            return newUploadToken;
        }

    }
}
