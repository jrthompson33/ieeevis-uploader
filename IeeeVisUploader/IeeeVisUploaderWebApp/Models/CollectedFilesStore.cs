/*
 * Copyright (c) Johannes Knittel
 *
 * This source code is licensed under the MIT license found in the
 * LICENSE file in the root directory of this source tree.
 */


using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

using Amazon;
using Amazon.S3;
using Amazon.S3.Model;

using IeeeVisUploaderWebApp.Helpers;

namespace IeeeVisUploaderWebApp.Models
{

    public class CollectedFilesStore
    {

        private readonly object _lck = new();
        private readonly object _saveLck = new();

        // Local dictionary stored in memory based on all the CollectedFile *.json in S3 bucket 
        private readonly Dictionary<string, List<CollectedFile>> _filesPerPaper = new();
        private long _version;
        private bool _savingFailed;

        private static readonly AmazonS3Client _s3Client = CreateS3Client();

        private static AmazonS3Client CreateS3Client()
        {
            var s3Client = new AmazonS3Client(DataProvider.Settings.AwsS3AccessKey, DataProvider.Settings.AwsS3SecretKey, RegionEndpoint.GetBySystemName(DataProvider.Settings.AwsS3Region));
            return s3Client;
        }

        private async Task UploadFileToS3(string keyName, string filePath)
        {
            try
            {
                var putRequest = new PutObjectRequest
                {
                    BucketName = DataProvider.Settings.AwsS3BucketName,
                    Key = $"collected_files/{DataProvider.Settings.BunnyBasePath}/collectedFiles.json",
                    FilePath = filePath,
                    ContentType = "text/plain"
                };

                PutObjectResponse response = await _s3Client.PutObjectAsync(putRequest);
            }
            catch (AmazonS3Exception e)
            {
                Console.WriteLine("Error encountered on server. Message:'{0}'", e.Message);
            }
            catch (Exception e)
            {
                Console.WriteLine("Unknown encountered on server. Message:'{0}'", e.Message);
            }
        }

        private async Task UploadStringContentAsFileToS3(string keyName, string content)
        {
            try
            {
                using (var ms = new MemoryStream(Encoding.UTF8.GetBytes(content)))
                {
                    var putRequest = new PutObjectRequest
                    {
                        BucketName = DataProvider.Settings.AwsS3BucketName,
                        Key = $"collected_files/{DataProvider.Settings.BunnyBasePath}/{keyName}.json",
                        InputStream = ms,
                        ContentType = "text/plain"
                    };

                    PutObjectResponse response = await _s3Client.PutObjectAsync(putRequest);
                    var statusCode = response.HttpStatusCode;
                }
            }
            catch (AmazonS3Exception e)
            {
                Console.WriteLine("Error encountered on server. Message:'{0}'", e.Message);
            }
            catch (Exception e)
            {
                Console.WriteLine("Unknown encountered on server. Message:'{0}'", e.Message);
            }
        }

        private async Task<List<string>> GetListOfCollectedFilesFromS3()
        {
            try
            {
                var listRequest = new ListObjectsRequest
                {
                    BucketName = DataProvider.Settings.AwsS3BucketName,
                    Prefix = $"collected_files/{DataProvider.Settings.BunnyBasePath}/",
                };

                ListObjectsResponse response = await _s3Client.ListObjectsAsync(listRequest);
                var listOfKeys = new List<string>();

                foreach (S3Object obj in response.S3Objects)
                {
                    listOfKeys.Add(obj.Key);
                }

                return listOfKeys;
            }
            catch (AmazonS3Exception e)
            {
                Console.WriteLine("Error encountered on server. Message:'{0}'", e.Message);
            }
            catch (Exception e)
            {
                Console.WriteLine("Unknown encountered on server. Message:'{0}'", e.Message);
            }
            // Return empty list if there was an exception
            return new List<string>();
        }

        private async Task<List<CollectedFile>> GetCollectedFileContentFromS3(string s3Key)
        {
            try
            {
                var getRequest = new GetObjectRequest
                {
                    BucketName = DataProvider.Settings.AwsS3BucketName,
                    Key = s3Key
                };
                using (GetObjectResponse response = await _s3Client.GetObjectAsync(getRequest))
                {
                    using (StreamReader reader = new StreamReader(response.ResponseStream))
                    {
                        string content = reader.ReadToEnd();
                        string[] fileJsonList = content.Split(",\n");
                        var collectedFiles = new List<CollectedFile>();
                        foreach (var fileJson in fileJsonList)
                        {
                            var fileJsonTrim = fileJson.Trim();
                            if (!string.IsNullOrEmpty(fileJsonTrim))
                            {
                                var collectedFile = JsonSerializer.Deserialize<CollectedFile>(fileJsonTrim);
                                collectedFiles.Add(collectedFile);
                            }
                        }
                        return collectedFiles;
                    }
                }

            }
            catch (AmazonS3Exception e)
            {
                Console.WriteLine("Error encountered on server. Message:'{0}'", e.Message);
            }
            catch (Exception e)
            {
                Console.WriteLine("Unknown encountered on server. Message:'{0}'", e.Message);
            }
            return null;
        }
        public CollectedFilesStore()
        {
            // No longer use local file, instead every time on startup, pull from all entries in S3 bucket
            // Get all the json files in S3 bucket, add those all to CollectedFiles
            GetListOfCollectedFilesFromS3().ContinueWith(
                (listTask) =>
                {
                    var s3Keys = listTask.Result;

                    foreach (var s3Key in s3Keys)
                    {
                        GetCollectedFileContentFromS3(s3Key).ContinueWith(
                            (getTask) =>
                            {
                                var files = getTask.Result;
                                if (files != null)
                                {
                                    foreach (var f in files)
                                    {
                                        if (f != null)
                                        {
                                            lock (_lck)
                                            {
                                                ref var list = ref CollectionsMarshal.GetValueRefOrAddDefault(_filesPerPaper, f.ParentUid, out _);
                                                if (list == null)
                                                    list = new List<CollectedFile> { f };
                                                else
                                                    list.Add(f);
                                            }
                                        }
                                    }
                                }
                            }
                        );
                    }
                }
            );
        }

        public Dictionary<string, List<CollectedFile>> GetDictionaryCopy()
        {
            lock (_lck)
            {
                return _filesPerPaper.ToDictionary(k => k.Key, k => k.Value.Select(it => it.Clone()).ToList());
            }
        }

        public List<CollectedFile> GetCollectedFilesCopy(string uid)
        {
            lock (_lck)
            {
                _filesPerPaper.TryGetValue(uid, out var l);
                if (l == null)
                    return new();
                return l.Select(it => it.Clone()).ToList();
            }
        }

        public CollectedFile? GetCollectedFileCopy(string uid, string itemId)
        {
            lock (_lck)
            {
                _filesPerPaper.TryGetValue(uid, out var l);
                return l?.FirstOrDefault(it => it.FileTypeId == itemId).Clone();
            }
        }

        public List<(string uid, List<CollectedFile> files)> GetPrefixCollectedFilesCopy(string eventId)
        {
            var res = new List<(string uid, List<CollectedFile> files)>();
            lock (_lck)
            {
                foreach (var (uid, l) in _filesPerPaper)
                {
                    if (!uid.StartsWith(eventId))
                        continue;
                    var files = l.Select(it => it.Clone()).ToList();
                    res.Add((uid, files));
                }
            }

            return res;
        }

        public List<CollectedFile> GetAllCollectedFilesCopy()
        {
            lock (_lck)
            {
                var res = new List<CollectedFile>();
                foreach (var l in _filesPerPaper.Values)
                {
                    foreach (var f in l)
                    {
                        res.Add(f.Clone());
                    }
                }

                return res;
            }
        }

        public void InsertOrUpdate(CollectedFile file)
        {
            var f = file.Clone();
            lock (_lck)
            {
                ref var list = ref CollectionsMarshal.GetValueRefOrAddDefault(_filesPerPaper, f.ParentUid, out _);
                if (list == null)
                {
                    list = new List<CollectedFile> { f };
                    return;
                }

                var updated = false;
                for (int i = 0; i < list.Count; i++)
                {
                    var ef = list[i];
                    if (ef.Name == f.Name)
                    {
                        list[i] = f;
                        updated = true;
                        break;
                    }
                }
                if (!updated)
                    list.Add(f);


                StringBuilder combinedSerializedStrings = new StringBuilder();
                foreach (CollectedFile fileItem in list)
                {
                    combinedSerializedStrings.Append(JsonSerializer.Serialize(fileItem, JsonSerializerOptions.Default));
                    combinedSerializedStrings.Append(",\n");
                }
                string combinedString = combinedSerializedStrings.ToString();

                UploadStringContentAsFileToS3(f.ParentUid, combinedString).Wait();

            }
        }

        public bool DeleteUid(string uid, bool onlyIfNoUploads = true)
        {
            lock (_lck)
            {
                ref var list = ref CollectionsMarshal.GetValueRefOrNullRef(_filesPerPaper, uid);
                if (Unsafe.IsNullRef(ref list))
                {
                    return false;
                }

                if (onlyIfNoUploads && list is { Count: > 0 })
                {
                    foreach (var f in list)
                    {
                        if (!string.IsNullOrEmpty(f.RawDownloadUrl))
                            return false;
                    }
                }

                _filesPerPaper.Remove(uid);

                // TODO also remove from the S3 bucket
                return true;
            }
        }

        public void SetFiles(string uid, List<CollectedFile> files)
        {
            files = files.Select(f => f.Clone()).ToList();

            lock (_lck)
            {
                _filesPerPaper[uid] = files;
            }
        }

        // public void EnsureStoreIsOnDisk()
        // {
        //     lock (_lck)
        //     {
        //         if (!_savingFailed)
        //             return;
        //     }

        //     Save();
        // }


        // public void Save()
        // {

        //     var tmpFn = _fileName + Guid.NewGuid().ToString("N");
        //     long version;
        //     List<CollectedFile> files;
        //     lock (_lck)
        //     {
        //         files = GetAllCollectedFilesCopy();
        //         version = ++_version;
        //     }

        //     try
        //     {
        //         File.WriteAllLines(tmpFn,
        //             files.Select(f => JsonSerializer.Serialize(f, JsonSerializerOptions.Default)));


        //         lock (_lck)
        //         {
        //             if (_version == version)
        //             {   
        //                 // UploadFileToS3(_fileName, tmpFn).Wait(); // Upload the entire collectedFiles.json to S3
        //                 File.Move(tmpFn, _fileName, true);
        //                 _savingFailed = false;
        //             }
        //         }
        //     }
        //     catch (Exception)
        //     {
        //         lock (_lck)
        //         {
        //             _savingFailed = true;
        //         }

        //         throw;
        //     }
        //     finally
        //     {
        //         try
        //         {
        //             if (File.Exists(tmpFn))
        //                 File.Delete(tmpFn);
        //         }
        //         catch (Exception)
        //         {
        //         }
        //     }

        // }

    }
}
