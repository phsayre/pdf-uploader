//#define MYDEBUG

using Npgsql;
using System;
using System.Collections.Generic;
using System.Configuration;
using System.Data;
using System.IO;
using System.Linq;
using System.Net.Mail;
using System.Text;
using System.Threading;


namespace PDFUploader
{
    class Uploader
    {
        private string finalDestPath; 
        private string connString; 
        private string emailHost;
        private string errorDir; 
        private string folderToScan;  
        private string fromAddress;  //for SendEmail()
        private string toAddress;  //for SendEmail()
        //the above variables are specified in app.config
        private int uploadedFiles;  //to keep track of how many files uploaded
        private FileInfo[] filesToUpload;
        List<FileInfo> failedUploads;  //to report failed uploads
        List<FileInfo> alreadyConverted;  //to report files that have already been uploaded once before
        private static NpgsqlConnection conn;

        
        public Uploader()
        {
            Setup();
        }

        /// <summary>
        /// Assigns variables according to app.config settings.
        /// </summary>
        private void Setup()
        {
#if MYDEBUG
            archivePath = @"C:\Program Files (x86)\neevia.com\docConverterPro\DEF_FOLDERS\UPLOADS\";
            connString = "someconnectionstring";
            folderToScan = @"C:\Program Files (x86)\neevia.com\docConverterPro\DEF_FOLDERS\OUT\";
#else
            try
            {
                alreadyConverted = new List<FileInfo>();
                connString = ConfigurationManager.AppSettings["connString"];
                emailHost = ConfigurationManager.AppSettings["emailHost"];
                errorDir = ConfigurationManager.AppSettings["errorDir"];
                failedUploads = new List<FileInfo>();
                finalDestPath = ConfigurationManager.AppSettings["finalDestPath"];
                folderToScan = ConfigurationManager.AppSettings["folderToScan"];
                fromAddress = ConfigurationManager.AppSettings["fromAddress"];
                toAddress = ConfigurationManager.AppSettings["toAddress"];
                conn = new NpgsqlConnection(connString);
                uploadedFiles = 0;
            }
            catch (Exception e)
            {
                WriteOut.HandleMessage("Could not set app settings: " + e.Message);
            }
#endif
            try
            {
                filesToUpload = new DirectoryInfo(folderToScan).GetFiles("*.*");
            }
            catch (Exception e)
            {
                WriteOut.HandleMessage("Could not access source directory: " + e.Message);
            }
        }


        private static bool SpecialBackslashes()
        {
            IDbCommand com = conn.CreateCommand();
            com.CommandText = "show standard_conforming_strings;";
            string standard_strings = com.ExecuteScalar().ToString();

            return (standard_strings == "off");
        }


        private static string SqlEscape(string query)
        {
            query = query.Replace("'", "''");
            if (SpecialBackslashes())
                query = query.Replace(@"\", @"\\");
            return query;
        }

        /// <summary>
        /// Checks if the folder to scan is empty.
        /// </summary>
        /// <returns>True if empty.</returns>
        private bool IsEmpty()
        {   
            bool isEmpty = !Directory.EnumerateFiles(folderToScan).Any();
            return isEmpty;
        }

        /// <summary>
        /// Checks the database converter_error flag on a given file.
        /// </summary>
        /// <param name="file">The file to check.</param>
        /// <returns>True when file converter_error=true.</returns>
        private bool IsError(FileInfo file)
        {
            bool isError;
            conn = new NpgsqlConnection(connString);
            conn.Open();
            NpgsqlCommand com = conn.CreateCommand();
            string itemID = Path.GetFileNameWithoutExtension(file.FullName);
            com.CommandText = String.Format("SELECT converter_error FROM redacted.items WHERE id={0};", itemID);
            string response = com.ExecuteScalar().ToString();
            conn.Close();
            if (response == "True")
            {
                isError = true;
            }
            else
            {
                isError = false;
            }
            return isError;
        }

        /// <summary>
        /// Archives a file.
        /// </summary>
        /// <param name="file">The file to archive.</param>
        private void ArchiveFile(FileInfo file)
        {
            string id = Path.GetFileNameWithoutExtension(file.FullName);
            string dest = finalDestPath + file.Name;
            int i = 0;
            try
            {
                file.MoveTo(dest);
            }
            catch
            {   
                while (File.Exists(finalDestPath + id + "_" + i + ".pdf"))
                {
                    i++;
                }
                file.MoveTo(finalDestPath + id + "_" + i + ".pdf");
            }
        }

        /// <summary>
        /// Moves files to the error directory and sets converter_error=true on the database.
        /// </summary>
        /// <param name="file">The file to move.</param>
        private void MoveToError(FileInfo file)
        {
            try
            {
                file.MoveTo(errorDir + file.Name);
            }
            catch
            {
                try
                {
                    File.Delete(errorDir + file.Name);
                    file.MoveTo(errorDir + file.Name);
                }
                catch (Exception e)
                {
                    WriteOut.HandleMessage("Error moving file to ERROR: " + e.Message);
                }
            }
        }


        /// <summary>
        /// Updates the database with error reported.
        /// </summary>
        /// <param name="file">The file to report.</param>
        private void UpdateDbOnError(FileInfo file)
        {
            string itemID = (Path.GetFileNameWithoutExtension(file.FullName));

            try
            {
                conn = new NpgsqlConnection(connString);
                conn.Open();
                NpgsqlCommand com = conn.CreateCommand();
                string msg = "Failed to upload:" + " (" + errorDir + file.Name + ")";
                com.CommandText = String.Format("UPDATE redacted.items SET converted=false, converter_error=true, converter_errormsg='{1}' WHERE id={0};", itemID, SqlEscape(msg));
                com.ExecuteNonQuery();
                conn.Close();
            }
            catch (Exception e)
            {
                WriteOut.HandleMessage("Could not connect to the database: " + e.Message);
            }
        }

        /// <summary>
        /// Updates the database with error reported on an already uploaded file.
        /// </summary>
        /// <param name="file">The file to report.</param>
        private void UpdateDbOnAlreadyConverted(FileInfo file)
        {
            string itemID = (Path.GetFileNameWithoutExtension(file.FullName));

            try
            {
                conn = new NpgsqlConnection(connString);
                conn.Open();
                NpgsqlCommand com = conn.CreateCommand();
                string msg = "Previously converted and uploaded. Stopped from re-uploading." + " (" + errorDir + file.Name + ")";
                com.CommandText = String.Format("UPDATE redacted.items SET converter_errormsg='{1}' WHERE id={0};", itemID, SqlEscape(msg));
                com.ExecuteNonQuery();
                conn.Close();
            }
            catch (Exception e)
            {
                WriteOut.HandleMessage("Could not connect to the database: " + e.Message);
            }
        }


        /// <summary>
        /// Generates the error message for the email.
        /// </summary>
        /// <param name="failedUploads">The failed uploads.</param>
        /// <returns>The message to email.</returns>
        private string GenerateErrorMessage(List<FileInfo> failedUploads)
        {
            string emailBody = "Errors occurred while attempting to upload the following files: \r";
            foreach (FileInfo file in failedUploads)
            {
                emailBody += "\r\n";
                emailBody += failedUploads.IndexOf(file).ToString() + ") ";
                emailBody += errorDir + file.Name;

                try
                {
                    string itemID = (Path.GetFileNameWithoutExtension(file.FullName));
                    conn = new NpgsqlConnection(connString);
                    conn.Open();
                    NpgsqlCommand com = conn.CreateCommand();
                    com.CommandText = String.Format("SELECT converter_errormsg FROM redacted.items WHERE id={0};", itemID);
                    string pgResponse = com.ExecuteScalar().ToString();
                    conn.Close();
                    emailBody += " | " + pgResponse;
                }
                catch (Exception e)
                {
                    WriteOut.HandleMessage("Error connecting to database: " + e.Message);
                }
            }
            return emailBody;
        }


        /// <summary>
        /// Sends the email using smtp.
        /// </summary>
        /// <param name="emailBody">The message to send.</param>
        /// <param name="subjectSelecter">To select the subject of the email.</param>
        private void SendEmail(string emailBody, int subjectSelecter)
        {
            string emailSubject = (subjectSelecter == 0) ? "PDF Upload Error: " + DateTime.Now.ToString() : "PDF Already Uploaded Error: " + DateTime.Now.ToString();

            try
            {
                SmtpClient myClient = new SmtpClient(emailHost);
                MailMessage myMessage = new MailMessage(fromAddress, toAddress, emailSubject, emailBody);
                myClient.Send(myMessage);
            }
            catch (Exception e)
            {
                WriteOut.HandleMessage("Error while attempting to send mail: " + e.Message);
            }
        }

        /// <summary>
        /// Checks if the file has been previously uploaded.
        /// </summary>
        /// <param name="file">The file to check.</param>
        /// <returns>True if the file has converted=true</returns>
        private bool IsAlreadyConverted(FileInfo file)
        {
            try
            {
                string itemID = Path.GetFileNameWithoutExtension(file.FullName);
                conn = new NpgsqlConnection(connString);
                conn.Open();
                NpgsqlCommand com = conn.CreateCommand();
                com.CommandText = String.Format("SELECT converted FROM redacted.items WHERE id={0}", itemID);
                string pgResponse = com.ExecuteScalar().ToString();

                if (pgResponse == "True")
                {
                    return true;
                }
                else
                {
                    return false;
                }
            }
            catch (Exception e)
            {
                WriteOut.HandleMessage("Error connecting to the database. Server said: " + e.Message);
                return false;
            }
        }


        /// <summary>
        /// Uploads the file to the database.
        /// </summary>
        /// <param name="file">The file to upload.</param>
        private void UploadFile(FileInfo file)
        {
            string base64;
            string itemID = Path.GetFileNameWithoutExtension(file.FullName);
            byte[] b = new byte[8192];
            int bytesRead;
            
            try
            {
                conn = new NpgsqlConnection(connString);
                conn.Open();
                NpgsqlCommand com = conn.CreateCommand();
                com.CommandText = String.Format("SELECT redacted.writeconvertedpdf({0}, '', true, false);", itemID);
                string pgResponse = com.ExecuteScalar().ToString();

                if (pgResponse != "ok")
                {
                    try
                    {
                        throw new Exception("Error when sending pdf to database server. Server said: " + pgResponse);
                    }
                    catch (Exception e)
                    {
                        WriteOut.HandleMessage(e.Message);
                    }
                }

                using (FileStream fs = File.OpenRead(file.FullName))
                {
                    while ((bytesRead = fs.Read(b, 0, b.Length)) > 0)
                    {
                        base64 = Convert.ToBase64String(b, 0, bytesRead);

                        com.CommandText = String.Format("SELECT redacted.writeconvertedpdf({0},'{1}', false, false);", itemID, base64);
                        pgResponse = com.ExecuteScalar().ToString();
                        if (pgResponse != "ok")
                        {
                            try
                            {
                                throw new Exception("Error when sending pdf to database server. Server said: " + pgResponse);
                            }
                            catch (Exception e)
                            {
                                WriteOut.HandleMessage(e.Message);
                            }
                        }
                    }
                }
                com.CommandText = String.Format("SELECT redacted.writeconvertedpdf({0}, '', false, true);", itemID);
                pgResponse = com.ExecuteScalar().ToString();
                if (pgResponse != "ok")
                {
                    try
                    {
                        throw new Exception("Error when sending pdf to database server. Server said: " + pgResponse);
                    }
                    catch (Exception e)
                    {
                        WriteOut.HandleMessage(e.Message);
                    }
                }
                conn.Close();
            }
            catch (Exception e)
            {
                WriteOut.HandleMessage("Errors occurred: " + e.Message);
                conn.Close();
                failedUploads.Add(file);
                return;
            }

            try
            {
                conn = new NpgsqlConnection(connString);
                conn.Open();
                NpgsqlCommand com = conn.CreateCommand();
                com.CommandText = String.Format("UPDATE redacted.items SET converted=true, converter_error=false, converter_errormsg='' WHERE id={0};", itemID);
                com.ExecuteNonQuery();
                conn.Close();
            }
            catch (Exception e)
            {
                WriteOut.HandleMessage("Could not update the database: " + e.Message);
            }
            ArchiveFile(file);
            uploadedFiles++;
        }

        /// <summary>
        /// Runs the program in logical order.
        /// When the folder-to-scan is empty the program does nothing.
        /// Files are uploaded one at a time.
        /// Errors are reported every time the program runs.
        /// </summary>
        public void Run()
        {

            if (IsEmpty())
            {
                //WriteOut.HandleMessage("Watch folder is empty; nothing to upload.");
                //do nothing
            }
            else
            {
                foreach (FileInfo file in filesToUpload)
                {
                    if (IsAlreadyConverted(file))
                    {
                        alreadyConverted.Add(file);
                        continue;
                    }
                    else if (IsError(file) == false)
                    {
                        UploadFile(file);
                    }
                    else
                    {
                        failedUploads.Add(file);
                        WriteOut.HandleMessage("Error: the file " + file.FullName + " is flagged as converter_error=true");
                    }
                }

                if(failedUploads.Count != 0)
                {
                    foreach (FileInfo file in failedUploads)
                    {
                        UpdateDbOnError(file);
                        MoveToError(file);
                    }
                    SendEmail(GenerateErrorMessage(failedUploads), 0);
                }

                if(alreadyConverted.Count != 0)
                {
                    foreach(FileInfo file in alreadyConverted)
                    {
                        UpdateDbOnAlreadyConverted(file);
                        MoveToError(file);
                    }
                    SendEmail(GenerateErrorMessage(alreadyConverted), 1);
                }

#if MYDEBUG
                Console.WriteLine("Press any key to exit.");
                Console.ReadKey();
#endif
                if(uploadedFiles != 0)
                {
                    WriteOut.HandleMessage(uploadedFiles.ToString() + " file(s) uploaded.");
                }
            }
        }
    }
}
