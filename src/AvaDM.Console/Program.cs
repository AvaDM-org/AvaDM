// See https://aka.ms/new-console-template for more information

using AvaDM.Core;

Console.WriteLine("AvaDM Console!");

// var input = args[1];
// if(string.IsNullOrEmpty(input))
//   Console.Write("Invalid Argument");
var uri = new Uri("https://dl2.soft98.ir/soft/w/WinRAR.7.23.0.x64.zip?1786116422");
var downloader = new Downloader();

await downloader.Download(uri);