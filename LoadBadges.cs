using Newtonsoft.Json;
using PaidPremiumAdmin.Business.FilesTransfer.AWSBucket;
using PaidPremiumAdmin.Business.Interfaces;
using PaidPremiumAdmin.Business.Services.Interfaces;
using PaidPremiumAdmin.Model.Tables;
using PaidPremiumAdmin.Model.ViewModel;
using ProcessingUtilities.Model;
using ProcessingUtilities.ServiceDataAccess;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ProcessingUtilities.Processing
{
    public class LoadBadges
    {
        AWSDataProvider _awsProvider;
        SolrPracticeCoreAccess _solrPract;
        SolrProviderSearchCoreAccess _searchAccess;
        IProviderService _providerService;
        IAWSDataProviderService _awsProviderService;
        IProviderDataProvider _dataProvider;
        IOfficeDataProvider _officeDataProvider;
        private const string LOG_FILE = "ProcessedPwids.txt";
        private const string LOG_BadgeException = "LogBadgesException.txt";
        private const string LOG_BadgeCompletionLog = "BadgeCompletionLog.txt";
        private static object count_lock = new object();
        private List<string> processedPwids = new List<string>();
        private const int REPORT_COUNT = 10;
        public LoadBadges(AWSDataProvider awsProvider, SolrPracticeCoreAccess solrAccess, SolrProviderSearchCoreAccess searchAccess, IProviderService providerService,
            IAWSDataProviderService awsProviderService, IProviderDataProvider dataProvider, IOfficeDataProvider officeDataProvider)
        {
            _awsProvider = awsProvider;
            _solrPract = solrAccess;
            _searchAccess = searchAccess;
            _providerService = providerService;
            _awsProviderService = awsProviderService;
            _dataProvider = dataProvider;
            _officeDataProvider = officeDataProvider;
        }

        public async Task<bool> PublishInvisalignBadges()
        {
            using (StreamWriter sw = new StreamWriter(LOG_BadgeCompletionLog, true))
            {
                sw.WriteLine(DateTime.Now.ToString());
                sw.WriteLine("-----------Processed Number of Providers");
            }
            Console.WriteLine("Start Processing ......." + DateTime.Now.ToString());
            var fileDataInvisalign = new List<InvisalignBadgeFileFormat>();
            Console.WriteLine("Parsing File");
            try
            {
                FileParserHelper parseInisalignFile = new FileParserHelper("invisalign_final.csv");
                fileDataInvisalign = parseInisalignFile.ParseInvisalignBadgeFile();
                Console.WriteLine("File Parsed" + fileDataInvisalign.Count.ToString());
            }
            catch (Exception ex)
            {
                Console.WriteLine("Error found");
                Console.WriteLine(ex.Message);
            }
            LoadProcessedPractices();
            int currentTask = 1;
            int maxProcessingCount = 10;

            List<Task> taskList = new List<Task>();
            if (fileDataInvisalign.Count > 0)
            {
                //Get provider info by npi
                int providerCount = 0;
                int currentCount = 0;
                foreach (var invisalignData in fileDataInvisalign)
                {
                    try
                    {
                        taskList.Add(Task.Factory.StartNew(async () =>
                        {
                            var providerViewModel = await _providerService.GetProviderByNpiAsync(invisalignData.Npi);

                            Console.WriteLine(providerViewModel.PWID);
                            //Create Provider BadgeSet
                            if (providerViewModel != null && providerViewModel.PWID != null && !processedPwids.Contains(providerViewModel.PWID))
                            {
                                var officeIds = new List<string>();
                                foreach (var practice in providerViewModel.PracticeOffices.PracticeOfficeList)
                                {
                                    foreach (var office in practice.Offices)
                                    {
                                        officeIds.Add(office.OfficeID);
                                    }
                                }

                                try
                                {
                                    CreateProviderBadgeSet(providerViewModel.PWID, officeIds);
                                    using (StreamWriter sw = new StreamWriter(LOG_FILE, true))
                                    {
                                        sw.WriteLine(providerViewModel.PWID);
                                    }
                                    bool entered = false;
                                    try
                                    {
                                        Monitor.TryEnter(count_lock, 1000, ref entered);
                                        if (entered)
                                        {
                                            if (currentCount % REPORT_COUNT == 0)
                                            {
                                                Console.WriteLine(string.Concat(currentCount, " ", DateTime.Now.ToString()));
                                            }
                                            currentCount++;
                                        }
                                    }
                                    finally
                                    {
                                        if (entered)
                                        {
                                            Monitor.Exit(count_lock);
                                        }
                                    }
                                    providerCount++;
                                    Console.WriteLine(providerCount.ToString());
                                }
                                catch (Exception ex)
                                {
                                    using (StreamWriter sw = new StreamWriter(LOG_BadgeException, true))
                                    {
                                        sw.WriteLine(ex.Message);
                                        sw.WriteLine("****************************************");
                                    }
                                }
                            }
                        }));

                        currentTask++;
                        if (currentTask > maxProcessingCount)
                        {
                            await Task.WhenAll(taskList);
                            taskList.Clear();
                            currentTask = 0;
                        }
                        System.Diagnostics.Debug.WriteLine(providerCount);
                    }
                    catch (Exception ex)
                    {
                        using (StreamWriter sw = new StreamWriter(LOG_BadgeException, true))
                        {
                            sw.WriteLine(ex.Message);
                            sw.WriteLine("****************************************");
                        }
                    }
                }
                using (StreamWriter sw = new StreamWriter(LOG_BadgeCompletionLog, true))
                {
                    sw.WriteLine(providerCount.ToString());
                    sw.WriteLine("Completed Time");
                    sw.WriteLine(DateTime.Now.ToString());
                }
                Console.WriteLine("Complete Processing ......." + DateTime.Now.ToString());
            }
            return true;
        }
        private SetViewModel CreateSetViewModelForInvisalign(string entityId)
        {
            var setViewModel = new SetViewModel();
            setViewModel.Pwid = entityId;
            setViewModel.Name = "Invisalign-Auto";
            setViewModel.ModifiedBy = "Automator";
            setViewModel.MediaType = PaidPremiumAdmin.Model.Enums.MediaTypeEnum.Badges;
            return setViewModel;
        }
        private void LoadProcessedPractices()
        {
            if (File.Exists(LOG_FILE))
            {
                using (var readFile = new StreamReader(LOG_FILE))
                {
                    while (readFile.Peek() != -1)
                    {
                        processedPwids.Add(readFile.ReadLine());
                    }
                }
            }
        }
        private bool CreateProviderBadgeSet(string pwid, List<string> officeIds)
        {
            bool setCreated = false;
            List<ProviderBadgeSet> providerBadgeSets = _dataProvider.GetProviderBadgeSetsByPwid(pwid);
            if (providerBadgeSets != null)
            {
                if (!providerBadgeSets.Where(x => x.BadgeSetName.Equals("Invisalign-Auto", StringComparison.InvariantCultureIgnoreCase)).Any())
                {
                    var setModel = CreateSetViewModelForInvisalign(pwid);

                    //TODO Fix this to return set Id
                    long badgeSetId = _dataProvider.UpsertProviderBadgeSet(setModel);

                    // Untill we fix sp to return set Id query db to get the set ID
                    List<ProviderBadgeSet> badgeSets = _dataProvider.GetProviderBadgeSetsByPwid(pwid);
                    var badgeSet = badgeSets.Where(x => x.BadgeSetName.Equals("Invisalign-Auto", StringComparison.InvariantCultureIgnoreCase)).FirstOrDefault();

                    // Publish both pop up and preview badges
                    var badgeToPublish = new View_Provider_BadgeSet();
                    badgeToPublish.PWID = pwid;
                    badgeToPublish.BadgeText = "<p><img alt=\"Invisalign\" src=\"https://d1uf6uw4rxe185.cloudfront.net/Provider/3s93z/Images/09e348c6-f356-45a1-bf57-fe661b03b7c2.png\" style=\"height:84px; width:350px\" /></p>\n\n<p>Invisalign clear aligners provide an effective way to straighten your teeth while remaining virtually invisible. Wearing the comfortable, customized aligners will gradually shift your teeth into their correct position. Correcting teeth alignment is imperative, not just cosmetically, but for overall health. Invisalign offers the best of both worlds: comfortable, efficient straightening with an essentially invisible appearance.</p>\n";
                    badgeToPublish.BadgeType = PaidPremiumAdmin.Model.Enums.BadgeType.Preview;

                    Console.WriteLine("Adding Badge for " + pwid);
                    badgeToPublish.BadgeSetID = badgeSet.ProviderBadgeSetID;
                    var badgeModelToSet = new AddBadgeModel();
                    badgeModelToSet.SelectedSet = setModel;
                    badgeModelToSet.SelectedBadge = badgeToPublish;
                    _dataProvider.UpsertProviderBadge(badgeModelToSet);

                    //Save Pop up badges;
                    //badgeModelToSet.SelectedBadge.BadgeText = "<p><img alt=\"Invisalign\" src=\"https://d1uf6uw4rxe185.cloudfront.net/Provider/3s93z/Images/09e348c6-f356-45a1-bf57-fe661b03b7c2.png\" style=\"height:84px; width:350px\" />&nbsp;</p>\n\n<p>Invisalign clear aligners provide an effective way to straighten your teeth while remaining virtually invisible. Wearing the comfortable, customized aligners will gradually shift your teeth into their correct position. Correcting teeth alignment is imperative, not just cosmetically, but for overall health. Invisalign offers the best of both worlds: comfortable, efficient straightening with an essentially invisible appearance.</p>\n";
                    //badgeModelToSet.SelectedBadge.BadgeType = PaidPremiumAdmin.Model.Enums.BadgeType.Popup;
                    //_dataProvider.UpsertProviderBadge(badgeModelToSet);
                    setCreated = true;
                    if (setCreated)
                    {
                        setModel.Id = pwid;
                        setModel.Type = "Provider";
                        setModel.SelectedSetId = badgeSet.ProviderBadgeSetID;
                        var setModels = new List<SetViewModel>();
                        setModels.Add(setModel);
                        foreach (string officeId in officeIds)
                        {
                            var setViewForOffice = new SetViewModel();
                            setViewForOffice = CreateSetViewModelForInvisalign(officeId);
                            setViewForOffice.SelectedSetId = badgeSet.ProviderBadgeSetID;
                            setViewForOffice.Type = "Office";
                            setViewForOffice.Id = officeId;
                            setModels.Add(setViewForOffice);
                        }

                        // Save the sets before publishing
                        foreach (SetViewModel setViewModel in setModels)
                        {
                            switch (setViewModel.Type)
                            {
                                case "Provider":
                                    _dataProvider.SaveProviderSelectedSet(setViewModel);
                                    break;
                                case "Practice":
                                    _officeDataProvider.SavePracticeSelectedSetSet(setViewModel);
                                    break;
                                case "Office":
                                    _officeDataProvider.SaveOfficeSelectedSet(setViewModel);
                                    break;
                            }
                        }
                        //Publish sets to provider and offices
                        _awsProviderService.PublishToSelectedEntities(setModels);

                        Console.WriteLine("Finished publishing badge for Office and Provider");
                    }
                };
            }
            return setCreated;
        }

    }
}
