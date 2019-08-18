using InstaSharper.API;
using InstaSharper.API.Builder;
using InstaSharper.Classes;
using System;
using System.Configuration;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Mail;
using System.Threading.Tasks;
using System.Xml;

namespace InstagramLooker
{
    class Program
    {
        static IInstaApi _instaApi;
        const string stateFile = "state.bin";
        static Configuration configFile = ConfigurationManager.OpenExeConfiguration(ConfigurationUserLevel.None);
        static DateTime now = DateTime.Now;
        static void Main(string[] args)
        {
            try
            {
                var result = Task.Run(MainAsync).GetAwaiter().GetResult();
                if (!result)
                    Environment.Exit(-1);
                //Console.ReadKey();
            }
            catch (Exception)
            {
                Environment.Exit(-1);
            }

        }
        public static async Task<bool> MainAsync()
        {
            var binFolder = configFile.AppSettings.Settings["BinFolder"].Value;
            Directory.CreateDirectory(binFolder);

            var hardFollows = configFile.AppSettings.Settings["HardFollows"].Value;
            var hardFollowList = hardFollows.Split(new[] { ";" }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).ToList();

            var userSession = new UserSessionData
            {
                UserName = configFile.AppSettings.Settings["UserName"].Value,
                Password = configFile.AppSettings.Settings["Password"].Value
            };
            var delay = RequestDelay.FromSeconds(2, 2);
            _instaApi = InstaApiBuilder.CreateBuilder().SetUser(userSession).SetRequestDelay(delay).Build();

            var stateFilePath = binFolder + stateFile;
            try
            {
                if (File.Exists(stateFilePath))
                {
                    //Console.WriteLine("Loading state from file");
                    using (var fs = File.OpenRead(stateFilePath))
                    {
                        _instaApi.LoadStateDataFromStream(fs);
                    }
                }
            }
            catch (Exception e)
            {
                return false;
            }

            if (!_instaApi.IsUserAuthenticated)
            {
                //Console.WriteLine($"Logging in as {userSession.UserName}");
                delay.Disable();
                var logInResult = await _instaApi.LoginAsync();
                delay.Enable();
                if (!logInResult.Succeeded)
                {
                    //Console.WriteLine($"Unable to login: {logInResult.Info.Message}");
                    return false;
                }

            }
            var state = _instaApi.GetStateDataAsStream();
            using (var fileStream = File.Create(stateFilePath))
            {
                state.Seek(0, SeekOrigin.Begin);
                state.CopyTo(fileStream);
            }

            var currentUser = await _instaApi.GetCurrentUserAsync();
            //Console.WriteLine($"Logged in: username - {currentUser.Value.UserName}, full name - {currentUser.Value.FullName}");
            //var followers = await _instaApi.GetCurrentUserFollowersAsync(PaginationParameters.MaxPagesToLoad(6));
            //Console.WriteLine($"Count of followers [{currentUser.Value.UserName}]:{followers.Value.Count}");

            var followers = await _instaApi.GetUserFollowersAsync(currentUser.Value.UserName, PaginationParameters.MaxPagesToLoad(6));
            var followersList = followers.Value.Select(p => p.UserName).ToList();

            var followersListPath = binFolder + @"FollowersLists\";
            Directory.CreateDirectory(followersListPath);
            var followerListFileFullName = followersListPath + "followersList" + now.ToString("yyyyMMddHHmmssFFFFFFF") + ".txt";
            File.WriteAllLines(followerListFileFullName, followersList);


            var following = await _instaApi.GetUserFollowingAsync(currentUser.Value.UserName, PaginationParameters.MaxPagesToLoad(6));
            var followingList = following.Value.Select(p => p.UserName).ToList();
            var followingListPath = binFolder + @"FollowingLists\";
            Directory.CreateDirectory(followingListPath);
            var followingListFileFullName = followingListPath + "followingList" + now.ToString("yyyyMMddHHmmssFFFFFFF") + ".txt";
            File.WriteAllLines(followingListFileFullName, followingList);

            var msgBody = PrepareMsgBody(followingListPath, followersListPath);

            if (msgBody != string.Empty)
            {
                var subject = "Analiz! InstagramLooker - " + now.ToString("dd/MM/yyyy - HH:mm");
                SendMail(subject, msgBody);
            }

            DeleteOldestFile(followersListPath);
            DeleteOldestFile(followingListPath);

            //Console.WriteLine($"Count of following [{currentUser.Value.UserName}]:{following.Value.Count}");

            return true;
        }
        static void DeleteOldestFile(string directory)
        {
            var dir = new DirectoryInfo(directory);
            var myFile = dir.GetFiles()
                         .OrderByDescending(f => f.Name)
                         .Skip(1);

            foreach (var fileInfo in myFile)
            {
                fileInfo.Delete();
            }
        }
        static void SendMail(string subject, string body)
        {
            var fromAddress = new MailAddress(configFile.AppSettings.Settings["FromMail"].Value, "InstagramLooker");
            string fromPassword = configFile.AppSettings.Settings["FromMailPassword"].Value;

            var confToAddr = configFile.AppSettings.Settings["MailTo"].Value;
            var to = confToAddr.Split(new[] { ";" }, StringSplitOptions.RemoveEmptyEntries);
            var toAddress = new MailAddress(to[0]);

            var smtp = new SmtpClient
            {
                Host = configFile.AppSettings.Settings["FromMailHost"].Value,
                Port = 587,
                EnableSsl = true,
                DeliveryMethod = SmtpDeliveryMethod.Network,
                Credentials = new NetworkCredential(fromAddress.Address, fromPassword),
                Timeout = 20000
            };
            using (var message = new MailMessage(fromAddress, toAddress)
            {
                Subject = subject,
                Body = body
            })
            {
                if (to.Count() > 1)
                    for (int i = 1; i < to.Count(); i++)
                        message.To.Add(new MailAddress(to[i].Trim()));

                smtp.Send(message);
            }
        }
        static string PrepareMsgBody(string followingListPath, string followersListPath)
        {
            var mailXml = new XmlDocument();
            var langCode = configFile.AppSettings.Settings["MailLangCode"].Value;
            mailXml.Load("MailTexts.xml");

            var returnString = string.Empty;

            var followingListDir = new DirectoryInfo(followingListPath);
            var oldFollowing = followingListDir.GetFiles()
                         .OrderByDescending(f => f.Name)
                         .Skip(1)
                         .First();
            var oldFollowingList = File.ReadAllLines(oldFollowing.FullName).ToList();
            var newFollowing = followingListDir.GetFiles()
                         .OrderByDescending(f => f.Name)
                         .First();
            var newFollowingList = File.ReadAllLines(newFollowing.FullName).ToList();

            var followersListDir = new DirectoryInfo(followersListPath);
            var oldFollowers = followersListDir.GetFiles()
                         .OrderByDescending(f => f.Name)
                         .Skip(1)
                         .First();
            var oldFollowersList = File.ReadAllLines(oldFollowers.FullName).ToList();
            var newFollowers = followersListDir.GetFiles()
                         .OrderByDescending(f => f.Name)
                         .First();
            var newFollowersList = File.ReadAllLines(newFollowers.FullName).ToList();

            var exceptFollowingList = oldFollowingList.Except(newFollowingList).ToList();
            var exceptFollowersList = oldFollowersList.Except(newFollowersList).ToList();

            var except2FollowingList = newFollowingList.Except(oldFollowingList).ToList();
            var except2FollowersList = newFollowersList.Except(oldFollowersList).ToList();

            exceptFollowingList = exceptFollowingList.Select(p => p + " - https://www.instagram.com/" + p + "/").ToList();
            exceptFollowersList = exceptFollowersList.Select(p => p + " - https://www.instagram.com/" + p + "/").ToList();

            except2FollowingList = except2FollowingList.Select(p => p + " - https://www.instagram.com/" + p + "/").ToList();
            except2FollowersList = except2FollowersList.Select(p => p + " - https://www.instagram.com/" + p + "/").ToList();

            if (exceptFollowingList.Count() > 0 || exceptFollowersList.Count() > 0
                || except2FollowingList.Count() > 0 || except2FollowersList.Count() > 0)
            {
                if (except2FollowersList.Count() > 0 || except2FollowingList.Count() > 0)
                    returnString += mailXml.SelectSingleNode($"/MailText/NewlyAddedCaption[@lang-code='{langCode}']").InnerText + Environment.NewLine;

                if (except2FollowersList.Count() > 0)
                {
                    returnString += mailXml.SelectSingleNode($"/MailText/NewFollowersListCaption[@lang-code='{langCode}']").InnerText + Environment.NewLine;
                    returnString += String.Join(Environment.NewLine, except2FollowersList) + Environment.NewLine;
                    returnString += Environment.NewLine;
                }

                if (except2FollowersList.Count() > 0)
                {
                    returnString += mailXml.SelectSingleNode($"/MailText/NewFollowingListCaption[@lang-code='{langCode}']").InnerText + Environment.NewLine;
                    returnString += String.Join(Environment.NewLine, except2FollowingList) + Environment.NewLine;
                    returnString += Environment.NewLine;
                }

                if (except2FollowersList.Count() > 0 || except2FollowingList.Count() > 0)
                    returnString += Environment.NewLine;


                if (exceptFollowersList.Count() > 0 || exceptFollowingList.Count() > 0)
                    returnString += mailXml.SelectSingleNode($"/MailText/NewlyLossesCaption[@lang-code='{langCode}']").InnerText + Environment.NewLine;

                if (exceptFollowersList.Count() > 0)
                {
                    returnString += mailXml.SelectSingleNode($"/MailText/StopFollowersListCaption[@lang-code='{langCode}']").InnerText + Environment.NewLine;
                    returnString += String.Join(Environment.NewLine, exceptFollowersList) + Environment.NewLine;
                    returnString += Environment.NewLine;
                }

                if (exceptFollowingList.Count() > 0)
                {
                    returnString += mailXml.SelectSingleNode($"/MailText/StopFollowingListCaption[@lang-code='{langCode}']").InnerText + Environment.NewLine;
                    returnString += String.Join(Environment.NewLine, exceptFollowingList) + Environment.NewLine;
                    returnString += Environment.NewLine;
                }

                //TODO: HardFollow

                returnString += $"-- {mailXml.SelectSingleNode($"/MailText/MailSignature[@lang-code='{langCode}']").InnerText} :)";
            }

            return returnString;
        }

    }
}
