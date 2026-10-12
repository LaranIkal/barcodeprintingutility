
/*
This program was designed by: Carlos Kassab(laran.ikal@gmail.com)
As is the program to send barcode label file to the printer, on github project:
https://github.com/LaranIkal/Infor_BaaN_LN_BarcodePrinting

The filewatcher has been programmed by ChatGPT AI.

Important Design: the current design uses both Windows file-change notifications and polling. 
It is not relying exclusively on polling, and it does not create a separate printing thread for every incoming file.

- At start time, The program deletes files already in the destination folder
- Everyday at 3 AM, CleanupDestinationFolder() deletes all files from previous day in destination folder
*/
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace barcodeutil;

internal class Program
{
  private static string WatchFolder = string.Empty;
  private static string DestinationFolder = string.Empty;
  private static string LogoFileName = string.Empty;

  private const string FileExtensionFilter = "*.barcode";

  private static readonly Encoding FileEncoding = Encoding.Latin1;
  private static string LogoFileContent = string.Empty;

  private static readonly string LogFilePath = Path.Combine( AppContext.BaseDirectory, "barcodeutil.log");

  private const long MaxLogSizeBytes = 1024 * 1024;
  // 10,000 items can be enqueued at once. If the queue is full, an error will be raised. 
  // So we can have a maximum of 10 thousand files waiting to be printed in the queue at the same time
  /*
    Very Important!!!: The queue is in memory, and the barcodeutil current startup cleanup deletes files already in the destination folder. 
    If the server restarts, a file that was moved but not printed successfully could be lost.
      If a server restart is needed, there two options, : 
      - Wait until all files are printed
      - Backup all barcodeutil directory and restore not printed files, after server restart.
  */
  private const int QueueCapacity = 10000; 
  private const int FileReadyAttempts = 20;
  private const int FileReadyDelayMilliseconds = 500;
  private const int PrintAttempts = 3;
  private const int PrintRetryDelayMilliseconds = 2000;
  private static readonly object LogLock = new();
  private static readonly BlockingCollection<string> PendingFiles = new(QueueCapacity);
  private static readonly ConcurrentDictionary<string, byte> QueuedFiles = new(StringComparer.OrdinalIgnoreCase);
  private static readonly ConcurrentDictionary<string, byte> ProcessingFiles = new(StringComparer.OrdinalIgnoreCase);
  private static readonly CancellationTokenSource CancellationSource = new();
  private static FileSystemWatcher? Watcher;
  private static DateTime LastCleanupDate = DateTime.MinValue;


  private static int Main(string[] args) {
    return RunAsync(args).GetAwaiter().GetResult();
  }

  
  private static async Task<int> RunAsync( string[] args) { 
    Log( "=== Barcode File Watcher starting ===" );
    if( !LoadConfiguration() ) return 1;

    Log( $"Watching:    {WatchFolder}" );
    Log( $"Destination: {DestinationFolder}" );
    Log( $"Logo:        {LogoFileName}" );
    Log( $"File filter: {FileExtensionFilter}" );
    Log( $"Queue size:  {QueueCapacity}" );

    try {
      Directory.CreateDirectory( WatchFolder );
      Directory.CreateDirectory( DestinationFolder );

      if( !File.Exists(LogoFileName) ) {
        Log( $"FATAL: Logo file not found: " + $"{LogoFileName}" );
        return 1;
      }

      LogoFileContent = File.ReadAllText( LogoFileName, FileEncoding );
    } catch( Exception ex ) {
      Log( $"FATAL: Initialization failed: " + $"{ex.Message}" );
      return 1;
    }

    CleanDestinationFolder(); // Clean temporary folder when starting barcodeutil

    AppDomain.CurrentDomain.ProcessExit += (_, _) => {
      try {
        CancellationSource.Cancel();
      } catch { }
    };

    StartWatcher();
    ScanExistingFiles();

    Task workerTask = ProcessQueueAsync( CancellationSource.Token );
    Task pollTask = PollLoopAsync( CancellationSource.Token );

    try {
      await Task.WhenAll( workerTask, pollTask );
    } catch( OperationCanceledException ) { }
    finally {
      PendingFiles.CompleteAdding();
      if( Watcher != null ) {
        Watcher.EnableRaisingEvents = false;
        Watcher.Dispose();
        Watcher = null;
      }

      Log( "=== Barcode File Watcher stopped ===" );
    }

      return 0;
  }


  private static bool LoadConfiguration() {
    string configFile = Path.Combine( AppContext.BaseDirectory, "barcodeutil.config" );

    try {
      if( !File.Exists(configFile) ) {
        Log( $"FATAL: Configuration file not found: {configFile}");
        return false;
      }

      foreach( string line in File.ReadAllLines(configFile) ) {
        string trimmedLine = line.Trim();

        if( string.IsNullOrWhiteSpace(trimmedLine) || trimmedLine.StartsWith("#") ) continue;

        int separator = trimmedLine.IndexOf('=');
        if( separator <= 0 ) continue;

        string key = trimmedLine[..separator].Trim();
        string value = trimmedLine[(separator + 1)..].Trim();
        switch( key ) {
          case "WatchFolder":
              WatchFolder = value;
              break;
          case "DestinationFolder":
              DestinationFolder = value;
              break;
          case "LogoFile":
              LogoFileName = value;
              break;
        }
      }

      if( string.IsNullOrWhiteSpace(WatchFolder) ) {
        Log( "FATAL: WatchFolder is missing from configuration.");
        return false;
      }

      if( string.IsNullOrWhiteSpace(DestinationFolder) ) {
        Log( "FATAL: DestinationFolder is missing from configuration." );
        return false;
      }

      if( string.IsNullOrWhiteSpace(LogoFileName) ) {
        Log( "FATAL: LogoFile is missing from configuration." );
        return false;
      }

      Log( $"Configuration loaded: {configFile}" );
      return true;
    } catch( Exception ex ) {
      Log( $"FATAL: Configuration error: {ex.Message}");
      return false;
    }
  }


  private static void StartWatcher() {
    try {
      Watcher = new FileSystemWatcher(WatchFolder) {
                      Filter = FileExtensionFilter,
                      NotifyFilter = NotifyFilters.FileName,
                      IncludeSubdirectories = false,
                      InternalBufferSize = 64 * 1024,
                      EnableRaisingEvents = true
                };

      Watcher.Created += (_, e) => { Enqueue(e.FullPath); };
      Watcher.Renamed += (_, e) => {
        if( e.FullPath.EndsWith( ".barcode", StringComparison.OrdinalIgnoreCase) ) Enqueue(e.FullPath);
      };

      Watcher.Error += (_, e) => {
        Log( $"[Watcher Error] {e.GetException()?.Message}");
      };

      Log( "FileSystemWatcher started.");
    } catch( Exception ex ) {
      Log( $"[Watcher Start Error] {ex.Message}");
      Watcher = null;
      Log( "Polling recovery remains active.");
    }
  }


  private static void ScanExistingFiles() {
    try {
      foreach( string file in Directory.EnumerateFiles( WatchFolder, FileExtensionFilter ) ) {
        Enqueue(file);
      }

      Log( "Initial folder scan completed." );
    } catch( Exception ex ) {
      Log( $"[Startup Scan] {ex.Message}" );
    }
  }


  private static async Task PollLoopAsync( CancellationToken token ) {
    using PeriodicTimer timer = new(TimeSpan.FromSeconds(5));

    try {
      while( await timer.WaitForNextTickAsync(token) ) {
        try {
          foreach( string file in Directory.EnumerateFiles( WatchFolder, FileExtensionFilter ) ) {
            Enqueue(file);
          }

          CleanupDestinationFolder();
        } catch( Exception ex ) {
          Log( $"[Poll] {ex.Message}");
        }
      }
    } catch( OperationCanceledException ) { }
  }


  private static void Enqueue( string filePath ) {
    if( PendingFiles.IsAddingCompleted ) return;

    if( !File.Exists(filePath) ) return;

    if( !QueuedFiles.TryAdd( filePath, 0 ) ) return;

    try {
      if( !PendingFiles.TryAdd(filePath) ) {
        QueuedFiles.TryRemove( filePath, out _ );

        Log( $"[Queue Full] Could not queue: {filePath}");
      }
    } catch( InvalidOperationException ) {
          QueuedFiles.TryRemove( filePath, out _ );
    }
  }


  private static async Task ProcessQueueAsync( CancellationToken token ) {
    try {
      foreach( string filePath in PendingFiles.GetConsumingEnumerable(token) ) {
        try {
          await ProcessFileAsync( filePath, token );
        } finally {
          QueuedFiles.TryRemove( filePath, out _ );
        }
      }
    } catch( OperationCanceledException ) { }
  }


  private static async Task ProcessFileAsync( string filePath, CancellationToken token ) {
    const int MoveAttempts = 10;

    try {
      if( !File.Exists(filePath) ) return;

      if( !await WaitForFileReadyAsync( filePath, token ) ) {
        Log( $"[File Not Ready] Gave up waiting for: {filePath}" );
        return;
      }

      string fileName = Path.GetFileName(filePath);
      string destinationPath = Path.Combine( DestinationFolder, fileName );

      for( int attempt = 1; attempt <= MoveAttempts; attempt++ ) {
        token.ThrowIfCancellationRequested();

        try {
          File.Move(filePath, destinationPath);

          Log( $"Moved: {fileName} -> {destinationPath}" );
          break;
        } catch( IOException ) when (attempt < MoveAttempts) {
          await Task.Delay( FileReadyDelayMilliseconds, token );
        } catch(UnauthorizedAccessException) when (attempt < MoveAttempts) {
          await Task.Delay( FileReadyDelayMilliseconds, token );
        }
      }

      if( File.Exists(filePath) ) {
        Log( $"[Move Failed] Could not move: {filePath}" );
        return;
      }

      ProcessingFiles.TryAdd( destinationPath, 0 );

      try {
        bool printed = await PrintLabelAsync( destinationPath, token );

        if(printed) { 
          Log( $"Printed: {fileName}" );
        } else {
          Log( $"[PRINT FAILED] File retained in destination folder: {destinationPath}");
        }
      } finally {
        ProcessingFiles.TryRemove( destinationPath, out _ );
      }
    } catch( OperationCanceledException ) {
      throw;
    } catch( Exception ex ) {
      Log( $"[File Error] {filePath}: {ex.Message}");
    }
  }


  private static async Task<bool> WaitForFileReadyAsync( string filePath, CancellationToken token ) {
    for( int attempt = 1; attempt <= FileReadyAttempts; attempt++ ) {
      token.ThrowIfCancellationRequested();

      try {
        if( !File.Exists(filePath) ) return false;

        FileInfo firstInfo = new(filePath);

        long firstLength = firstInfo.Length;

        DateTime firstWriteTime = firstInfo.LastWriteTimeUtc;

        await Task.Delay( 200, token );

        if( !File.Exists(filePath) ) return false;

        FileInfo secondInfo = new(filePath);

        if( firstLength != secondInfo.Length || firstWriteTime != secondInfo.LastWriteTimeUtc ) {
          await Task.Delay( FileReadyDelayMilliseconds, token );
          continue;
        }

        using FileStream stream = new( filePath, FileMode.Open, FileAccess.Read, FileShare.None );
        return true;
      } catch(IOException) { }
        catch( UnauthorizedAccessException ) { }

        await Task.Delay( FileReadyDelayMilliseconds,token );
    }

    return false;
  }


  private static async Task<bool> PrintLabelAsync( string spoolFile, CancellationToken token ) {
    string SpoolToPrint;
    string content;
    string temporaryPrintFile = string.Empty;

    try {
      using StreamReader reader = new( spoolFile, FileEncoding );
      reader.ReadLine();

      string? printerPath = reader.ReadLine();

      if( string.IsNullOrWhiteSpace( printerPath )) {
        Log( $"[Label Processing Error] Printer path is missing from second line: {spoolFile}" );
        return false;
      }

      SpoolToPrint = printerPath.Trim();

      content = reader.ReadToEnd();

      const string LogoMarker = "***COMPANYLOGO***";

      if( content.Contains( LogoMarker, StringComparison.Ordinal ) ) {
        content = content.Replace( LogoMarker, LogoFileContent, StringComparison.Ordinal );
      }

      temporaryPrintFile = Path.Combine( Path.GetTempPath(), $"barcodeutil_{Guid.NewGuid():N}.tmp");

      File.WriteAllText( temporaryPrintFile, content, FileEncoding );

      Log( $"Printer: {SpoolToPrint}" );
    } catch( Exception ex ) {
      Log( $"[Label Processing Error] {spoolFile}: {ex.Message}");
      return false;
    }

    try {
      for( int attempt = 1; attempt <= PrintAttempts; attempt++ ) {
        token.ThrowIfCancellationRequested();

        try {
          File.Copy( temporaryPrintFile, SpoolToPrint, overwrite: true );
          return true;
        } catch( Exception ex ) {
          Log( $"[Print Attempt {attempt}/{PrintAttempts}] {Path.GetFileName(spoolFile)} -> {SpoolToPrint}: {ex.Message}" );
          if( attempt < PrintAttempts ) {
            await Task.Delay( PrintRetryDelayMilliseconds, token );
          }
        }
      }

      return false;
    } finally {
      try {
        if( !string.IsNullOrEmpty( temporaryPrintFile ) ) {
          File.Delete( temporaryPrintFile );
          //Log($"*** Temporary print file created: {temporaryPrintFile}" );
        }
      } catch { }
    }
  }


  private static void CleanDestinationFolder() {
    int deleted = 0;
    int skipped = 0;
    int failed = 0;

    try {
      foreach( string file in Directory.EnumerateFiles( DestinationFolder ) ) {
        if( ProcessingFiles.ContainsKey(file) ) {
          skipped++;
          continue;
        }

        try {
          File.Delete(file);
          deleted++;
        } catch( Exception ex ) {
          failed++;

          Log( $"[Startup Cleanup] Cannot delete '{file}': {ex.Message}" );
        }
      }

      Log( $"[Startup Cleanup] Deleted: {deleted}, Skipped: {skipped}, Failed: {failed}" );
    } catch( Exception ex ) {
      Log( $"[Startup Cleanup] Error: {ex.Message}" );
    }
  }


  private static void CleanupDestinationFolder() {
    DateTime now = DateTime.Now;

    if( now.Hour < 3 ) return;

    if( LastCleanupDate == now.Date ) return;

    LastCleanupDate = now.Date;

    DateTime today = now.Date;

    int deleted = 0;
    int skipped = 0;
    int failed = 0;

    try {
      foreach( string file in Directory.EnumerateFiles(DestinationFolder) ) {
        if( ProcessingFiles.ContainsKey(file) ) {
          skipped++;
          continue;
        }

        try {
          if( File.GetLastWriteTime(file) < today ) {
            File.Delete(file);
            deleted++;
          }
        } catch( Exception ex ) {
          failed++;
          Log( $"[Destination Cleanup] Cannot delete '{file}': {ex.Message}");
        }
      }

      Log( $"[Destination Cleanup] Deleted: {deleted}, Skipped: {skipped}, Failed: {failed}" );
    } catch( Exception ex ) {
      Log( $"[Destination Cleanup] Error: {ex.Message}" );
    }
  }


  private static void Log( string message ) {
    string line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] " + message;

    lock(LogLock) {
      try {
        RollLogIfNeeded();
        File.AppendAllText( LogFilePath, line + Environment.NewLine );
      } catch { }
    }
  }


  private static void RollLogIfNeeded() {
    try {
      if( !File.Exists(LogFilePath) ) return;

      FileInfo info = new(LogFilePath);

      if( info.Length < MaxLogSizeBytes ) return;

      string archivePath = Path.Combine( AppContext.BaseDirectory, $"BarcodeWatcher_{DateTime.Now:yyyyMMdd_HHmmss}.log");

      File.Move( LogFilePath, archivePath, overwrite: true );
    } catch { }
  }


}


