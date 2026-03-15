using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using NLog;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Http;
using NzbDrone.Core.Books.Calibre;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Core.ImportLists.Calibre
{
    public class CalibreImport : ImportListBase<CalibreImportSettings>
    {
        private const int PAGE_SIZE = 750;
        private readonly IHttpClient _httpClient;

        public CalibreImport(IHttpClient httpClient,
                             IImportListStatusService importListStatusService,
                             IConfigService configService,
                             IParsingService parsingService,
                             Logger logger)
            : base(importListStatusService, configService, parsingService, logger)
        {
            _httpClient = httpClient;
        }

        public override string Name => "Calibre";

        public override ImportListType ListType => ImportListType.Program;

        public override TimeSpan MinRefreshInterval => TimeSpan.FromHours(12);

        public override IList<ImportListItemInfo> Fetch()
        {
            var result = new List<ImportListItemInfo>();

            try
            {
                var ids = GetAllBookIds();

                _logger.Info("Calibre Import: Found {0} total book IDs", ids.Count);

                var offset = 0;
                var emptyCount = 0;

                while (offset < ids.Count)
                {
                    var batch = ids.Skip(offset).Take(PAGE_SIZE).ToList();
                    var books = GetBooks(batch);

                    foreach (var book in books.Values)
                    {
                        if (book.Formats != null && book.Formats.Any())
                        {
                            continue;
                        }

                        var author = book.Authors?.FirstOrDefault();
                        if (author.IsNullOrWhiteSpace())
                        {
                            _logger.Debug("Calibre Import: Skipping book {0} ({1}) - no author", book.Id, book.Title);
                            continue;
                        }

                        var item = new ImportListItemInfo
                        {
                            Author = author,
                            Book = book.Title,
                        };

                        if (book.Identifiers != null)
                        {
                            if (book.Identifiers.TryGetValue("hardcover-id", out var hardcoverId))
                            {
                                item.BookGoodreadsId = hardcoverId;
                            }
                            else if (book.Identifiers.TryGetValue("goodreads", out var goodreadsId))
                            {
                                item.BookGoodreadsId = goodreadsId;
                            }
                        }

                        result.Add(item);
                        emptyCount++;
                    }

                    offset += PAGE_SIZE;
                }

                _logger.Info("Calibre Import: Found {0} empty entries out of {1} total books", emptyCount, ids.Count);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Calibre Import: Failed to fetch books");
                throw;
            }

            return CleanupListItems(result);
        }

        protected override void Test(List<FluentValidation.Results.ValidationFailure> failures)
        {
            try
            {
                GetAllBookIds();
            }
            catch (Exception ex)
            {
                failures.Add(new FluentValidation.Results.ValidationFailure("BaseUrl", "Unable to connect to Calibre Content Server: " + ex.Message));
            }
        }

        private List<int> GetAllBookIds()
        {
            var builder = GetBuilder("/ajax/category/616c6c626f6f6b73/" + Settings.Library);
            var offset = 0;
            var ids = new List<int>();

            while (true)
            {
                builder.AddQueryParam("num", PAGE_SIZE, replace: true);
                builder.AddQueryParam("offset", offset, replace: true);

                var request = builder.Build();
                var response = _httpClient.Get<CalibreCategory>(request);

                if (!response.Resource.BookIds.Any())
                {
                    break;
                }

                ids.AddRange(response.Resource.BookIds);
                offset += PAGE_SIZE;
            }

            return ids;
        }

        private Dictionary<int, CalibreBook> GetBooks(List<int> ids)
        {
            var builder = GetBuilder("/ajax/books/" + Settings.Library);
            builder.LogResponseContent = false;
            builder.AddQueryParam("ids", ids.Select(x => x.ToString()).ConcatToString(","));

            var request = builder.Build();
            var response = _httpClient.Get<Dictionary<int, CalibreBook>>(request);

            return response.Resource;
        }

        private HttpRequestBuilder GetBuilder(string relativePath)
        {
            var baseUrl = HttpRequestBuilder.BuildBaseUrl(Settings.UseSsl, Settings.BaseUrl, Settings.Port, Settings.UrlBase);
            baseUrl = HttpUri.CombinePath(baseUrl, relativePath);

            var builder = new HttpRequestBuilder(baseUrl)
                .Accept(HttpAccept.Json);

            if (Settings.Username.IsNotNullOrWhiteSpace())
            {
                builder.NetworkCredential = new NetworkCredential(Settings.Username, Settings.Password);
            }

            return builder;
        }
    }
}
