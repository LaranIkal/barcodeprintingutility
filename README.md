# barcodeprintingutility
Watch for barcode files in a directory and send them to the barcode printer
Please read this guide before using barcodeutil:
https://github.com/LaranIkal/barcodeprintingutility/blob/main/barcodeutil_operation_and_capacity_guideV3.pdf




<img width="432" height="480" alt="BarCodePrintingSolution" src="https://github.com/user-attachments/assets/97d72c85-1e4f-4831-87a6-0d00def9d04f" />


This program was designed by: Carlos Kassab(laran.ikal@gmail.com)
As is the program to send barcode label file to the printer, on github project:
https://github.com/LaranIkal/Infor_BaaN_LN_BarcodePrinting

The filewatcher has been programmed by ChatGPT AI.

Important Design: the current design uses both Windows file-change notifications and polling. 
It is not relying exclusively on polling, and it does not create a separate printing thread for every incoming file.

- At start time, The program deletes files already in the destination folder
- Everyday at 3 AM, CleanupDestinationFolder() deletes all files from previous day in destination folder

