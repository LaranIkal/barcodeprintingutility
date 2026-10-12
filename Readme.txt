Deployment

The resulting deployment is very simple:

C:\barcodeutilserver\
    barcodeutil.exe
    barcodeutil.config
    CompanyLogo.prn
    barcodeutil.log


Moving the utility to another server could require only
changing in the barcodeutil.config, example:

WatchFolder=D:\Barcode\Incoming
DestinationFolder=D:\Barcode\Processing
LogoFile=D:\Barcode\CompanyLogo.prn


.barcode arrives
    ↓
Line 1 = ignored
Line 2 = \\PrintingServer\BarcodePrintingQueue
    ↓
Lines 3+ = printer data
    ↓
Logo replacement, if required
    ↓
Temporary print file containing ONLY lines 3+
    ↓
File.Copy(temp file, \\PrintingServer\BarcodePrintingQueue)


