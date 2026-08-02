/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */

namespace Listenarr.Tests.Features.Infrastructure.Metadata.Providers.Goodreads;

public class GoodreadsListReaderTests
{
    [Fact]
    public void ParseCsv_ReadsGoodreadsExportRows()
    {
        const string csv =
            "Book Id,Title,Author,ISBN,ISBN13,Original Publication Year,Bookshelves\r\n" +
            "1,\"The Left Hand of Darkness\",\"Ursula K. Le Guin\",\"=\"\"0441478123\"\"\",\"=\"\"9780441478125\"\"\",1969,\"sci-fi, favorites\"\r\n" +
            "2,\"A \"\"Quoted\"\" Book\",\"Example Author\",,,2024,to-read\r\n";

        var books = GoodreadsListReader.ParseCsv(csv);

        Assert.Equal(2, books.Count);
        Assert.Equal("The Left Hand of Darkness", books[0].Title);
        Assert.Equal("Ursula K. Le Guin", books[0].Author);
        Assert.Contains("0441478123", books[0].Isbn);
        Assert.Contains("9780441478125", books[0].Isbn);
        Assert.Equal("1969", books[0].PublishYear);
        Assert.Equal("sci-fi, favorites", books[0].Bookshelf);
        Assert.Equal("A \"Quoted\" Book", books[1].Title);
    }

    [Fact]
    public void ParseHtml_ReadsPublicShelfBookRows()
    {
        const string html = """
            <table>
              <tr class="bookalike review">
                <td><a class="bookTitle" href="/book/show/12345.Test_Book"><span>Test Book</span></a></td>
                <td><a class="authorName"><span>Test Author</span></a></td>
              </tr>
            </table>
            """;

        var books = GoodreadsListReader.ParseHtml(html, new Uri("https://www.goodreads.com/review/list/1"));

        var book = Assert.Single(books);
        Assert.Equal("12345", book.GoodreadsId);
        Assert.Equal("Test Book", book.Title);
        Assert.Equal("Test Author", book.Author);
        Assert.Equal("https://www.goodreads.com/book/show/12345.Test_Book", book.SourceUrl);
    }
}
