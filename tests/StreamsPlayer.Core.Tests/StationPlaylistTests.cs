using StreamsPlayer.Core;

namespace StreamsPlayer.Core.Tests;

/// <summary>SP-0121: a station given as a playlist link records the stream the playlist points to (C-09).</summary>
public sealed class StationPlaylistTests
{
    private static readonly Uri ListAddress = new("http://radio.example/tunein/station.pls");

    [Fact]
    public void PlsTakesTheFirstFileEntry()
    {
        const string body = "[playlist]\r\nNumberOfEntries=2\r\nTitle1=Station\r\nFile1=http://s1.example:8000/live\r\nFile2=http://s2.example:8000/live\r\nLength1=-1\r\n";
        Assert.Equal(new Uri("http://s1.example:8000/live"), StationPlaylist.FirstStream(body, ListAddress));
    }

    [Fact]
    public void M3uSkipsCommentsAndTakesTheFirstAddress()
    {
        const string body = "#EXTM3U\n#EXTINF:-1,Station\nhttps://s1.example/live.mp3\nhttps://s2.example/live.mp3\n";
        Assert.Equal(new Uri("https://s1.example/live.mp3"), StationPlaylist.FirstStream(body, ListAddress));
    }

    [Fact]
    public void RelativeEntryResolvesAgainstThePlaylist() =>
        Assert.Equal(new Uri("http://radio.example/tunein/live.aac"), StationPlaylist.FirstStream("live.aac\n", ListAddress));

    [Fact]
    public void NonHttpEntriesAreSkipped() =>
        Assert.Equal(new Uri("http://ok.example/a"), StationPlaylist.FirstStream("File1=mms://x.example/a\nFile2=http://ok.example/a\n", ListAddress));

    [Fact]
    public void AListWithNothingPlayableIsNull() =>
        Assert.Null(StationPlaylist.FirstStream("[playlist]\nNumberOfEntries=0\n", ListAddress));

    [Theory]
    [InlineData("[playlist]\nFile1=http://a/b", true)]
    [InlineData("#EXTM3U\n", true)]
    [InlineData("﻿http://a.example/b\n", true)]
    [InlineData("<html><body>Not found</body></html>", false)]
    public void RecognisesPlaylistText(string text, bool expected) =>
        Assert.Equal(expected, StationPlaylist.LooksLikePlaylist(text));

    [Fact]
    public void M3uAddressLineWithQueryStringIsTakenWhole() =>
        Assert.Equal(new Uri("http://host/stream?type=mp3&session=1"),
            StationPlaylist.FirstStream("http://host/stream?type=mp3&session=1\n", ListAddress));

    [Fact]
    public void PlsFileEntryWithQueryStringIsTakenWhole()
    {
        const string body = "[playlist]\nFile1=http://s1.example/live?type=mp3\n";
        Assert.Equal(new Uri("http://s1.example/live?type=mp3"), StationPlaylist.FirstStream(body, ListAddress));
    }

    [Fact]
    public void HtmlBodyServedAsAPlaylistYieldsNothing() =>
        Assert.Null(StationPlaylist.FirstStream(
            "<!DOCTYPE html>\n<html>\n<head><title>404 Not Found</title></head>\n<body>Not Found</body>\n</html>\n",
            ListAddress));
}
