﻿using OpenImis.ePayment.Escape.Payment.Models;
using OpenImis.ePayment.Data;
using OpenImis.ePayment.Logic;
using OpenImis.ePayment.Models;
using OpenImis.ePayment.Models.Payment;
using OpenImis.ePayment.Models.Payment.Response;
using OpenImis.ePayment.Responses;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;
using OpenImis.DB.SqlServer;
using Microsoft.Extensions.Logging;

namespace OpenImis.ePayment.Data
{
    public class ImisBasePayment
    {
        public int PaymentId { get; set; }
        public decimal ExpectedAmount { get; set; }
        public string ControlNum { get; set; }
        public string PhoneNumber { get; set; }
        public Language Language { get; set; }
        public TypeOfPayment? typeOfPayment { get; set; }

        public DateTime? PaymentDate { get; set; }
        public decimal? PaidAmount { get; set; }
        public decimal? OutStAmount { get; set; }
        public bool SmsRequired { get; set; }
        public string Location { get; set; }
        public List<InsureeProduct> InsureeProducts { get; set; }

        protected IConfiguration Configuration;
        protected readonly IHostingEnvironment _hostingEnvironment;
        protected DataHelper dh;
        protected ILoggerFactory _loggerFactory;
        protected ILogger _logger;

        public ImisBasePayment(IConfiguration configuration, IHostingEnvironment hostingEnvironment, ILoggerFactory loggerFactory)
        {
            Configuration = configuration;
            _hostingEnvironment = hostingEnvironment;
            dh = new DataHelper(configuration);
            _loggerFactory = loggerFactory;
            _logger = loggerFactory.CreateLogger<ImisBasePayment>();
        }

        public async Task<bool> SaveControlNumberRequest(int BillId, bool failed)
        {

            SqlParameter[] sqlParameters = {
                new SqlParameter("@PaymentID", BillId),
                new SqlParameter("@Failed", failed)
             };

            try
            {
                var data = await dh.ExecProcedureAsync("uspRequestGetControlNumber", sqlParameters);
                GetPaymentInfo(BillId);
            }
            catch (Exception e)
            {
                throw e;
            }

            return true;
        }

        public virtual async Task<PostReqCNResponse> PostReqControlNumberAsync(string OfficerCode, int PaymentId, string PhoneNumber, decimal ExpectedAmount, List<PaymentDetail> products, string controlNumber = null, bool acknowledge = false, bool error = false, string rejectedReason = "")
        {
            bool result = await SaveControlNumberRequest(PaymentId, error);
            string ctrlNumber = null;
#if !CHF
            // BEGIN Temporary Control Number Generator(Simulation For Testing Only)
            var randomNumber = new Random().Next(100000, 999999);
            ctrlNumber = randomNumber.ToString();
            //END Temporary 
#endif
            PostReqCNResponse response = new PostReqCNResponse()
            {

                ControlNumber = ctrlNumber,
                Posted = error == false ? true : false,
                ErrorCode = 0,
                ErrorOccured = error,
                Assigned = error,
                ErrorMessage = rejectedReason
            };

            return response;
        }

        public virtual async Task<bool> UpdatePaymentTransferFeeAsync(int paymentId, decimal TransferFee, TypeOfPayment typeOfPayment)
        {

            var sSQL = @"UPDATE tblPayment SET TypeOfPayment = @TypeOfPayment,TransferFee = @TransferFee WHERE PaymentID = @paymentId";

            SqlParameter[] parameters = {
                new SqlParameter("@paymentId", paymentId),
                new SqlParameter("@TransferFee", TransferFee),
                new SqlParameter("@TypeOfPayment", Enum.GetName(typeof(TypeOfPayment),typeOfPayment))
            };

            try
            {
                await dh.ExecuteAsync(sSQL, parameters, CommandType.Text);

                return true;
            }
            catch (Exception)
            {
                return false;
            }

        }

        public virtual decimal determineTransferFee(decimal expectedAmount, TypeOfPayment typeOfPayment)
        {
            return 0;
        }

        public virtual decimal determineTransferFeeReverse(decimal expectedAmount, TypeOfPayment typeOfPayment)
        {
            return 0;
        }

        public virtual int GetReqControlNumberAck(int paymentId)
        {
            return 0;
        }

        public virtual int GetPaymentDataAck(int paymentId, string controlNumber)
        {
            return 0;
        }

        public virtual decimal GetToBePaidAmount(decimal ExpectedAmount, decimal TransferFee)
        {
            decimal amount = ExpectedAmount - TransferFee;
            return Math.Round(amount, 2);
        }

        public async Task<DataMessage> SaveIntentAsync(IntentOfPay _intent, int? errorNumber = 0, string errorMessage = null)
        {
            var Proxyfamily = LocalDefault.FamilyMembers(Configuration);

            var policies = new List<PaymentDetail>();

            if (_intent.policies != null)
            {
                policies = _intent.policies;
            }

            XElement PaymentIntent = new XElement("PaymentIntent",
                    new XElement("Header",
                        new XElement("OfficerCode", _intent.enrolment_officer_code),
                        new XElement("RequestDate", _intent.request_date),
                        new XElement("PhoneNumber", _intent.phone_number),
                        new XElement("LanguageName", _intent.language),
                        new XElement("SmsRequired", Convert.ToInt32(_intent.SmsRequired)),
                        new XElement("AuditUserId", -1)
                    ),
                      new XElement("Details",
                         policies.Select(x =>

                               new XElement("Detail",
                                  new XElement("InsuranceNumber", x.insurance_number),
                                  new XElement("ProductCode", x.insurance_product_code),
                                  new XElement("PolicyValue", x.amount),
                                  new XElement("EnrollmentDate", DateTime.UtcNow),
                                  new XElement("IsRenewal", x.IsRenewal())
                                  )
                    )
                  ),
                   new XElement("ProxySettings",
                        new XElement("AdultMembers", Proxyfamily.Adults),
                        new XElement("ChildMembers", Proxyfamily.Children),
                        new XElement("OAdultMembers", Proxyfamily.OtherAdults),
                        new XElement("OChildMembers", Proxyfamily.OtherChildren)
                   )
            );



            SqlParameter[] sqlParameters = {
                new SqlParameter("@Xml", PaymentIntent.ToString()),
                new SqlParameter("@ErrorNumber",errorNumber),
                new SqlParameter("@ErrorMsg",errorMessage),
                new SqlParameter("@PaymentID", SqlDbType.Int){Direction = ParameterDirection.Output },
                new SqlParameter("@ExpectedAmount", SqlDbType.Decimal){Direction = ParameterDirection.Output },
                new SqlParameter("@ProvidedAmount",_intent.amount_to_be_paid),
                new SqlParameter("@PriorEnrollment",LocalDefault.PriorEnrolmentRequired(Configuration))
             };

            DataMessage message;

            try
            {
                bool error = true;
                var data = await dh.ExecProcedureAsync("uspInsertPaymentIntent", sqlParameters);

                var rv = int.Parse(data[2].Value.ToString());

                if (rv == 0)
                {
                    error = false;
                }

                DataTable dt = new DataTable();
                dt.Clear();
                dt.Columns.Add("internal_identifier");
                dt.Columns.Add("control_number");

                DataRow rw = dt.NewRow();
                int PaymentId = (int)data[0].Value;
                rw["internal_identifier"] = PaymentId;

                dt.Rows.Add(rw);

                ExpectedAmount = decimal.Parse(data[1].Value.ToString());

                var languages = LocalDefault.PrimaryLanguageRepresentations(Configuration);

                if (_intent.language == null || languages.Contains(_intent.language.ToLower()))
                {
                    Language = Language.Primary;
                }
                else
                {
                    Language = Language.Secondary;
                }

                message = new SaveIntentResponse(rv, error, dt, (int)Language).Message;
                GetPaymentInfo(PaymentId);
            }
            catch (Exception e)
            {
                message = new SaveIntentResponse(e).Message;
            }

            return message;
        }

        public async Task<DataMessage> SaveControlNumberAsync(string ControlNumber, bool failed)
        {
            SqlParameter[] sqlParameters = {
                new SqlParameter("@PaymentID", PaymentId),
                new SqlParameter("@ControlNumber", ControlNumber),
                new SqlParameter("@Failed", failed)
             };

            DataMessage message;

            try
            {
                var data = await dh.ExecProcedureAsync("uspReceiveControlNumber", sqlParameters);
                message = new CtrlNumberResponse(int.Parse(data[0].Value.ToString()), false, (int)Language).Message;
                GetPaymentInfo(PaymentId);
            }
            catch (Exception e)
            {
                message = new CtrlNumberResponse(e).Message;
            }

            return message;
        }

        public async Task<DataMessage> SaveControlNumberAsync(ControlNumberResp model, bool failed)
        {
            SqlParameter[] sqlParameters = {
                new SqlParameter("@PaymentID", model.internal_identifier),
                new SqlParameter("@ControlNumber", model.control_number),
                new SqlParameter("@Failed", failed),
                //new SqlParameter("@Message", model.error_message)
             };

            DataMessage message;

            try
            {
                var data = await dh.ExecProcedureAsync("uspReceiveControlNumber", sqlParameters);
                message = new CtrlNumberResponse(int.Parse(data[0].Value.ToString()), false, (int)Language).Message;
                GetPaymentInfo(model.internal_identifier);
            }
            catch (Exception e)
            {
                message = new CtrlNumberResponse(e).Message;
            }

            return message;
        }

        public bool CheckControlNumber(int PaymentID, string ControlNumber)
        {
            var sSQL = @"SELECT * FROM tblControlNumber WHERE PaymentID != @PaymentID AND ControlNumber = @ControlNumber";

            SqlParameter[] parameters = {
                new SqlParameter("@PaymentID", PaymentID),
                new SqlParameter("@ControlNumber", ControlNumber)
            };
            bool result = false;

            try
            {
                var data = dh.GetDataTable(sSQL, parameters, CommandType.Text);
                if (data.Rows.Count > 0)
                {
                    result = true;
                }
                //GetPaymentInfo(PaymentID);
            }
            catch (Exception)
            {
                return false;
            }

            return result;
        }

        public async void UpdateControlNumberStatusAsync(string ControlNumber, CnStatus status)
        {

            SqlParameter[] sqlParameters = {
                  new SqlParameter("@ControlNumber", ControlNumber)
            };

            switch (status)
            {
                case CnStatus.Sent:
                    break;
                case CnStatus.Acknowledged:
                    await dh.ExecProcedureAsync("uspAcknowledgeControlNumber", sqlParameters);
                    break;
                case CnStatus.Issued:
                    await dh.ExecProcedureAsync("uspIssueControlNumber", sqlParameters);
                    break;
                case CnStatus.Paid:
                    await dh.ExecProcedureAsync("uspPaidControlNumber", sqlParameters);
                    break;
                case CnStatus.Rejected:
                    break;
                default:
                    break;
            }
        }

        public async Task<DataMessage> SaveControlNumberAknAsync(bool error_occured, string Comment)
        {
            XElement CNAcknowledgement = new XElement("ControlNumberAcknowledge",
                  new XElement("PaymentID", PaymentId),
                  new XElement("Success", Convert.ToInt32(!error_occured)),
                  new XElement("Comment", Comment)
                  );

            SqlParameter[] sqlParameters = {
                new SqlParameter("@Xml",CNAcknowledgement.ToString())
             };


            DataMessage message;

            try
            {
                var data = await dh.ExecProcedureAsync("uspAcknowledgeControlNumberRequest", sqlParameters);
                message = new SaveAckResponse(int.Parse(data[0].Value.ToString()), false, (int)Language).Message;
                GetPaymentInfo(PaymentId);
            }
            catch (Exception e)
            {
                message = new SaveAckResponse(e).Message;
            }

            return message;
        }


        public async Task<DataMessage> SavePaymentAsync(PaymentData payment, bool failed = false)
        {
            int? isRenewal = null;

            if (payment.renewal != null)
            {
                isRenewal = (int)payment.renewal;
            }

            var paymentId = GetPaymentId(payment.control_number);

            XElement PaymentIntent = new XElement("PaymentData",
                new XElement("PaymentID", paymentId),
                new XElement("PaymentDate", payment.payment_date),
                new XElement("ReceiveDate", payment.received_date),
                new XElement("ControlNumber", payment.control_number),
                new XElement("Amount", payment.received_amount),
                new XElement("ReceiptNo", payment.receipt_identification),
                new XElement("TransactionNo", payment.transaction_identification),
                new XElement("PhoneNumber", payment.payer_phone_number),
                new XElement("PaymentOrigin", payment.payment_origin),
                new XElement("OfficerCode", payment.enrolment_officer_code),
                new XElement("LanguageName", payment.language), // not used
                new XElement("Detail",
                    new XElement("InsureeNumber", payment.insurance_number),
                    new XElement("ProductCode", payment.insurance_product_code),
                    new XElement("IsRenewal", isRenewal)
                            )
               );


            SqlParameter[] sqlParameters = {
                new SqlParameter("@Xml", PaymentIntent.ToString()),
                new SqlParameter("@Payment_ID",SqlDbType.BigInt){Direction = ParameterDirection.Output }
             };

            DataMessage message;

            try
            {
                var data = await dh.ExecProcedureAsync("uspReceivePayment", sqlParameters);
                // TODO: manage error messages from SP execution 
                message = new SavePayResponse(int.Parse(data[1].Value.ToString()), false, (int)Language).Message;
                GetPaymentInfo(Convert.ToInt32(data[0].Value));

            }
            catch (Exception e)
            {
                message = new SavePayResponse(e).Message;
            }

            return message;
        }

        public DataMessage MatchPayment(MatchModel model)
        {
            SqlParameter[] sqlParameters = {
                new SqlParameter("@PaymentID", model.internal_identifier),
                new SqlParameter("@AuditUserId", model.audit_user_id)
             };

            DataMessage message;

            try
            {
                DataSet data = dh.FillDataSet("uspMatchPayment", sqlParameters, CommandType.StoredProcedure);

                //bool error = false;
                DataTable dt = new DataTable();

                if (data.Tables.Count > 0)
                {
                    dt = data.Tables[data.Tables.Count - 1];

                    //    error = true;

                    //    if (dt.Rows.Count > 0)
                    //    {
                    //        var firstRow = dt.Rows[0];

                    //        if (Convert.ToInt32(firstRow["PaymentMatched"]) > 0)
                    //        {
                    //            error = false;
                    //        }

                    //    }
                    //    else
                    //    {
                    //        error = true;
                    //    }
                }

                message = new MatchPayResponse(dh.ReturnValue, false, dt, (int)Language).Message;
                if (model.internal_identifier != 0 && !message.ErrorOccured)
                {
                    GetPaymentInfo(model.internal_identifier);
                }
            }
            catch (Exception e)
            {
                message = new ImisApiResponse(e).Message;
            }

            return message;
        }


        public async Task<DataMessage> GetControlNumbers(string PaymentIds)
        {

            var sSQL = String.Format(@"SELECT PaymentID,ControlNumber
                         FROM tblControlNumber WHERE PaymentID IN({0})", PaymentIds);

            SqlParameter[] sqlParameters = {

             };

            DataMessage dt = new DataMessage();
            try
            {
                DataTable data = dh.GetDataTable(sSQL, sqlParameters, CommandType.Text);
                data.Columns["PaymentID"].ColumnName = "internal_identifier";
                data.Columns["ControlNumber"].ColumnName = "control_number";

                var ids = PaymentIds.Split(",");

                if (ids.Distinct().Count() == data.Rows.Count)
                {
                    dt = new RequestedCNResponse(0, false, data, (int)Language).Message;
                }
                else
                {
                    var _datastring = JsonConvert.SerializeObject(data);
                    var _data = JsonConvert.DeserializeObject<List<AssignedControlNumber>>(_datastring);
                    var _ids = _data.Select(x => x.internal_identifier).ToArray();

                    DataTable invalid = new DataTable();
                    invalid.Clear();
                    invalid.Columns.Add("internal_identifier");
                    invalid.Columns.Add("control_number");

                    foreach (var id in ids.Except(_ids))
                    {
                        DataRow rw = invalid.NewRow();

                        rw["internal_identifier"] = id;

                        invalid.Rows.Add(rw);
                    }

                    dt = new RequestedCNResponse(2, true, invalid, (int)Language).Message;
                }

            }
            catch (Exception e)
            {
                throw e;
            }

            return await Task.FromResult(dt);
        }

        public void GetPaymentInfo(int Id)
        {
            var sSQL = @"DECLARE @MatchedPoliciesInPayment TABLE (PolicyId int)

                        INSERT INTO @MatchedPoliciesInPayment
                        SELECT DISTINCT(Premium.PolicyId)
                        FROM tblPayment AS Payment
                        INNER JOIN tblPaymentDetails AS PaymentDetails ON PaymentDetails.PaymentID=Payment.PaymentID
                        INNER JOIN tblPremium AS Premium ON Premium.PremiumID=PaymentDetails.PremiumID
                        WHERE (Payment.PaymentID = @PaymentID)

                        SELECT Payment.PaymentID, Payment.ExpectedAmount, Payment.LanguageName, Payment.TypeOfPayment, 
                        Payment.SmsRequired, PaymentDetails.ExpectedAmount AS ExpectedDetailAmount, Payment.ReceivedAmount, 
                        Payment.PaymentDate, Insuree.LastName, Insuree.OtherNames, PaymentDetails.InsuranceNumber, 
                        Payment.PhoneNumber, Payment.PayerPhoneNumber, Product.ProductName, PaymentDetails.ProductCode, 
                        Policy.ExpiryDate, Policy.EffectiveDate, ControlNumber.ControlNumber, Policy.PolicyStatus, 
                        Policy.PolicyValue - ISNULL(MatchPayments.PrPaid,0) Outstanding, 
                        CASE WHEN Region.LocationId IS NULL THEN District.LocationName ELSE Region.LocationName END AS Location
                        FROM tblPayment AS Payment
                        LEFT JOIN tblPaymentDetails AS PaymentDetails ON PaymentDetails.PaymentID=Payment.PaymentID
                        LEFT JOIN tblInsuree AS Insuree ON Insuree.CHFID=PaymentDetails.InsuranceNumber
                        LEFT JOIN tblProduct AS Product ON Product.ProductCode=PaymentDetails.ProductCode
                        LEFT JOIN tblLocations AS District ON Product.LocationId=District.LocationId AND District.LocationType='D'
                        LEFT JOIN tblLocations AS Region ON Product.LocationId=Region.LocationId AND Region.LocationType='R'
                        LEFT JOIN tblControlNumber AS ControlNumber ON ControlNumber.PaymentID=Payment.PaymentID
                        LEFT JOIN tblPremium AS Premium ON Premium.PremiumID=PaymentDetails.PremiumID
                        LEFT JOIN tblPolicy AS Policy ON Policy.PolicyID=Premium.PolicyID
                        LEFT JOIN (SELECT P.PolicyID AS PolicyID, SUM(P.Amount) PrPaid 
		                        FROM tblPremium P 
		                        INNER JOIN tblPaymentDetails PD ON PD.PremiumID = P.PremiumId 
		                        INNER JOIN tblPayment Pay ON Pay.PaymentID = PD.PaymentID  
		                        WHERE P.PolicyID IN (SELECT PolicyId FROM @MatchedPoliciesInPayment) AND Pay.PaymentStatus = 5 AND P.ValidityTo IS NULL
		                        GROUP BY P.PolicyID) AS MatchPayments 
	                        ON MatchPayments.PolicyID=Policy.PolicyId
                        WHERE (Payment.PaymentID = @PaymentID) AND (Product.ValidityTo IS NULL) AND (Insuree.ValidityTo IS NULL)
                        ";

            SqlParameter[] parameters = {
                new SqlParameter("@PaymentID", Id)
            };

            try
            {
                var data = dh.GetDataTable(sSQL, parameters, CommandType.Text);

                if (data.Rows.Count > 0)
                {
                    var row1 = data.Rows[0];
                    PaymentId = Id;
                    ControlNum = row1["ControlNumber"] != System.DBNull.Value ? Convert.ToString(row1["ControlNumber"]) : null;
                    ExpectedAmount = row1["ExpectedAmount"] != System.DBNull.Value ? Convert.ToDecimal(row1["ExpectedAmount"]) : 0;
                    SmsRequired = row1["SmsRequired"] != System.DBNull.Value ? Convert.ToBoolean(row1["SmsRequired"]) : false;
                    Location = row1["Location"] != System.DBNull.Value ? Convert.ToString(row1["Location"]) : null;

                    var language = row1["LanguageName"] != System.DBNull.Value ? Convert.ToString(row1["LanguageName"]) : "en";
                    var languages = LocalDefault.PrimaryLanguageRepresentations(Configuration);

                    if (language == null || languages.Contains(language.ToLower()))
                    {
                        Language = Language.Primary;
                    }
                    else
                    {
                        Language = Language.Secondary;
                    }
                    typeOfPayment = row1["TypeOfPayment"] != System.DBNull.Value ? (TypeOfPayment?)Enum.Parse(typeof(TypeOfPayment), Convert.ToString(row1["TypeOfPayment"]), true) : null;

                    PhoneNumber = row1["PhoneNumber"] != System.DBNull.Value ? Convert.ToString(row1["PhoneNumber"]) : null;
                    PaymentDate = (DateTime?)(row1["PaymentDate"] != System.DBNull.Value ? row1["PaymentDate"] : null);
                    PaidAmount = (decimal?)(row1["ReceivedAmount"] != System.DBNull.Value ? row1["ReceivedAmount"] : null);
                    OutStAmount = (decimal?)(row1["Outstanding"] != System.DBNull.Value ? row1["Outstanding"] : null);
                    InsureeProducts = new List<InsureeProduct>();

                    for (int i = 0; i < data.Rows.Count; i++)
                    {
                        var rw = data.Rows[i];

                        bool active = false;

                        if (rw["PolicyStatus"] != System.DBNull.Value && Convert.ToInt32(rw["PolicyStatus"]) == 2)
                        {
                            active = true;
                        }
                        var othernames = rw["OtherNames"] != System.DBNull.Value ? Convert.ToString(rw["OtherNames"]) : null;
                        var lastname = rw["LastName"] != System.DBNull.Value ? Convert.ToString(rw["LastName"]) : null;
                        InsureeProducts.Add(
                                new InsureeProduct()
                                {

                                    InsureeNumber = rw["InsuranceNumber"] != System.DBNull.Value ? Convert.ToString(rw["InsuranceNumber"]) : null,
                                    InsureeName = othernames + " " + lastname,
                                    ProductName = rw["ProductName"] != System.DBNull.Value ? Convert.ToString(rw["ProductName"]) : null,
                                    ProductCode = rw["ProductCode"] != System.DBNull.Value ? Convert.ToString(rw["ProductCode"]) : null,
                                    ExpiryDate = (DateTime?)(rw["ExpiryDate"] != System.DBNull.Value ? rw["ExpiryDate"] : null),
                                    EffectiveDate = (DateTime?)(rw["EffectiveDate"] != System.DBNull.Value ? rw["EffectiveDate"] : null),
                                    PolicyActivated = active,
                                    ExpectedProductAmount = rw["ExpectedDetailAmount"] != System.DBNull.Value ? Convert.ToDecimal(rw["ExpectedDetailAmount"]) : 0
                                }
                            );
                    }

                }
                else
                {
                    throw new DataException("3-Wrong Control Number");
                }
            }
            catch (Exception e)
            {
                throw e;
            }

        }

        public List<PaymentDetail> GetPaymentDetails(int Id)
        {
            List<PaymentDetail> PaymentDetails = new List<PaymentDetail>();

            var sSQL = @"SELECT PaymentDetails.PaymentDetailsID, PaymentDetails.ExpectedAmount, PaymentDetails.InsuranceNumber, PaymentDetails.ProductCode
                            FROM tblPayment AS Payment
                            LEFT JOIN tblPaymentDetails AS PaymentDetails ON PaymentDetails.PaymentID=Payment.PaymentID
                            WHERE Payment.PaymentID = @PaymentID AND Payment.ValidityTo IS NULL
                        ";

            SqlParameter[] parameters = {
                new SqlParameter("@PaymentID", Id)
            };

            try
            {
                var data = dh.GetDataTable(sSQL, parameters, CommandType.Text);

                if (data.Rows.Count > 0)
                {

                    for (int i = 0; i < data.Rows.Count; i++)
                    {
                        var rw = data.Rows[i];

                        PaymentDetails.Add(
                                new PaymentDetail()
                                {
                                    payment_detail_id = rw["PaymentDetailsID"] != System.DBNull.Value ? Convert.ToInt32(rw["PaymentDetailsID"]) : 0,
                                    insurance_number = rw["InsuranceNumber"] != System.DBNull.Value ? Convert.ToString(rw["InsuranceNumber"]) : null,
                                    insurance_product_code = rw["ProductCode"] != System.DBNull.Value ? Convert.ToString(rw["ProductCode"]) : null,
                                    amount = rw["ExpectedAmount"] != System.DBNull.Value ? Convert.ToDecimal(rw["ExpectedAmount"]) : 0
                                }
                            );
                    }
                }
            }
            catch (Exception e)
            {
                throw e;
            }

            return PaymentDetails;
        }

        public List<MatchSms> GetPaymentIdsForSms()
        {

            var sSQl = @"SELECT tblPayment.PaymentID,tblPayment.DateLastSMS,tblPayment.MatchedDate
                        FROM tblControlNumber 
                        RIGHT OUTER JOIN tblInsuree 
                        RIGHT OUTER JOIN tblProduct 
                        RIGHT OUTER JOIN tblPayment 
                        INNER JOIN tblPaymentDetails 
                        ON tblPayment.PaymentID = tblPaymentDetails.PaymentID 
                        ON tblProduct.ProductCode = tblPaymentDetails.ProductCode 
                        ON tblInsuree.CHFID = tblPaymentDetails.InsuranceNumber 
                        ON tblControlNumber.PaymentID = tblPayment.PaymentID 
                        LEFT OUTER JOIN tblPremium 
                        LEFT OUTER JOIN tblPolicy 
                        ON tblPremium.PolicyID = tblPolicy.PolicyID 
                        ON tblPaymentDetails.PremiumID = tblPremium.PremiumId
                        WHERE (tblProduct.ValidityTo IS NULL) AND (tblInsuree.ValidityTo IS NULL)
						AND tblPayment.PaymentStatus >= 4 AND tblPayment.PaymentStatus < 5";

            SqlParameter[] parameters = { };

            try
            {
                var data = dh.GetDataTable(sSQl, parameters, CommandType.Text);
                List<MatchSms> Ids = null;
                if (data.Rows.Count > 0)
                {
                    var jsonString = JsonConvert.SerializeObject(data);
                    Ids = JsonConvert.DeserializeObject<List<MatchSms>>(jsonString);
                }

                return Ids;
            }
            catch (Exception e)
            {
                throw e;
            }
        }

        public async void UpdateLastSMSSentDateAsync()
        {
            var sSQL = @"UPDATE tblPayment
                         SET DateLastSMS = CURRENT_TIMESTAMP
                         WHERE PaymentID = @PaymentID;";

            SqlParameter[] parameters = {
                new SqlParameter("@PaymentID", PaymentId)
            };

            try
            {
                await dh.ExecuteAsync(sSQL, parameters, CommandType.Text);
            }
            catch (Exception)
            {
                throw;
            }
        }

        public int GetPaymentId(string ControlNumber)
        {
            var sSQL = @"SELECT PaymentID FROM tblControlNumber WHERE ControlNumber = @ControlNumber";

            SqlParameter[] parameters = {
                new SqlParameter("@ControlNumber", ControlNumber)
            };
            try
            {
                var data = dh.GetDataTable(sSQL, parameters, CommandType.Text);
                if (data.Rows.Count > 0)
                {
                    var row = data.Rows[0];
                    return Convert.ToInt32(row["PaymentID"]);
                }
                //GetPaymentInfo(PaymentID);
            }
            catch (Exception)
            {
                return 0;
            }
            return 0;
        }

        public List<ReconciliationItem> ProvideReconciliationData(ReconciliationRequest model)
        {
            List<ReconciliationItem> result = new List<ReconciliationItem>();

            DateTime startDate = DateTime.ParseExact(model.date_from, "ddMMyyyy", System.Globalization.CultureInfo.InvariantCulture);
            DateTime endDate = DateTime.ParseExact(model.date_to, "ddMMyyyy", System.Globalization.CultureInfo.InvariantCulture);

            SqlParameter[] parameters = {
                new SqlParameter("@DateFrom", startDate),
                new SqlParameter("@DateTo", endDate)
            };

            var sSQL = @"SELECT * FROM tblPayment WHERE ReceivedDate BETWEEN @DateFrom AND @DateTo";

            try
            {
                var data = dh.GetDataTable(sSQL, parameters, CommandType.Text);

                if (data.Rows.Count > 0)
                {
                    for (int i = 0; i < data.Rows.Count; i++)
                    {
                        var rw = data.Rows[i];

                        result.Add(new ReconciliationItem
                        {
                            control_number = rw["PaymentID"].ToString(),
                            enrolment_officer_code = rw["OfficerCode"].ToString(),
                            insurance_number = rw["PaymentID"].ToString(),
                            language = rw["LanguageName"] != System.DBNull.Value ? Convert.ToString(rw["LanguageName"]) : null,
                            payment_date = rw["PaymentDate"] != System.DBNull.Value ? DateTime.Parse(rw["PaymentDate"].ToString()).ToString("ddMMyyyy") : null,
                            payment_origin = rw["PaymentOrigin"].ToString(),
                            receipt_identification = rw["ReceiptNo"].ToString(),
                            received_amount = rw["ReceivedAmount"] != System.DBNull.Value ? Convert.ToDouble(rw["ReceivedAmount"]) : 0,
                            received_date = rw["ReceivedDate"] != System.DBNull.Value ? DateTime.Parse(rw["ReceivedDate"].ToString()).ToString("ddMMyyyy") : null,
                            transaction_identification = rw["TransactionNo"].ToString(),
                            type_of_payment = rw["TypeOfPayment"] != System.DBNull.Value ? Convert.ToString(rw["TypeOfPayment"]) : null
                        });
                    }
                }
            }
            catch (Exception)
            {
                throw;
            }

            return result;
        }

        public async Task CancelPayment(int payment_id)
        {
            var sSQL = @"UPDATE tblPayment
                         SET ValidityTo = CURRENT_TIMESTAMP
                         WHERE PaymentID = @PaymentID;";

            SqlParameter[] parameters = {
                new SqlParameter("@PaymentID", payment_id)
            };

            try
            {
                await dh.ExecuteAsync(sSQL, parameters, CommandType.Text);
            }
            catch (Exception)
            {
                throw;
            }
        }

        public async void updateReconciliatedPaymentAsync(string billId, int reconId)
        {
            var sSQL = @"UPDATE tblPayment
                         SET SpReconcReqId = @SpReconcReqId,
                         ReconciliationDate = GETDATE()
                         WHERE PaymentID = @PaymentID;";

            SqlParameter[] parameters = {
                new SqlParameter("@PaymentID", billId),
                new SqlParameter("@SpReconcReqId", reconId)
            };

            try
            {
                await dh.ExecuteAsync(sSQL, parameters, CommandType.Text);
            }
            catch (Exception)
            { 
                throw;
            }
        }

        public async void updateReconciliatedPaymentError(string billId, int reconId)
        {
            var sSQL = @"UPDATE tblPayment
                         SET SpReconcReqId = @SpReconcReqId,
                         ReconciliationDate = GETDATE()
                         WHERE PaymentID = @PaymentID;";

            SqlParameter[] parameters = {
                new SqlParameter("@PaymentID", billId),
                new SqlParameter("@SpReconcReqId", reconId)
            };

            try
            {
                await dh.ExecuteAsync(sSQL, parameters, CommandType.Text);
            }
            catch (Exception)
            {

                throw;
            }
        }

        public bool CheckPaymentExistError(string id)
        {
            var sSQL = @"SELECT PaymentID FROM tblPayment WHERE PaymentID = @paymentId And PaymentStatus<>@PaymentStatus And ValidityTo is Null";

            SqlParameter[] parameters = {
                new SqlParameter("@paymentId", id),
                new SqlParameter("@PaymentStatus", PaymentStatus.FailedMatchPayment)
            };
            try
            {
                var data = dh.GetDataTable(sSQL, parameters, CommandType.Text);
                if (data.Rows.Count > 0)
                {
                    var row = data.Rows[0];
                    if (row["PaymentID"].ToString() == id)
                    {
                        return true;
                    }
                }
            }
            catch (Exception)
            {
                return false;
            }

            return false;
        }

        public bool Exists()
        {
            var sSQL = @"SELECT PaymentID FROM tblPayment WHERE PaymentID = @paymentId And ValidityTo is Null";

            SqlParameter[] parameters = {
                new SqlParameter("@paymentId", this.PaymentId)
            };
            try
            {
                var data = dh.GetDataTable(sSQL, parameters, CommandType.Text);
                if (data.Rows.Count > 0)
                {
                    var row = data.Rows[0];
                    if (int.Parse(row["PaymentID"].ToString()) == this.PaymentId)
                    {
                        return true;
                    }
                }
            }
            catch (Exception)
            {
                return false;
            }

            return false;
        }

        public async void setRejectedReason(int billId, string rejectedReason)
        {
            var sSQL = @"UPDATE tblPayment
                         SET RejectedReason = CASE WHEN ISNULL(RejectedReason, '') = '' Then @RejectedReason ELSE Concat(RejectedReason,Concat(';', @RejectedReason)) END
                         WHERE PaymentID = @PaymentID;";

            SqlParameter[] parameters = {
                new SqlParameter("@PaymentID", billId),
                new SqlParameter("@RejectedReason", rejectedReason)
            };

            try
            {
                await dh.ExecuteAsync(sSQL, parameters, CommandType.Text);
            }
            catch (Exception)
            {
                throw;
            }
        }

        public TblOfficer GetOfficerInfo(int officerId)
        {
            var context = new ImisDB();
            return context.TblOfficer.Where(o => o.OfficerId == officerId).FirstOrDefault();

        }

        public virtual async Task<string> RequestBulkControlNumbers(RequestBulkControlNumbersModel model)
        {
            return await Task.FromResult("");
        }

        public virtual BulkControlNumbersForEO GetControlNumbersForEO(string officerCode, string productCode, int available)
        {
            return new BulkControlNumbersForEO();
        }

        public virtual async Task<int> ControlNumbersToBeRequested(string productCode)
        {
            return await Task.FromResult(0);
        }


        public virtual int CreatePremium(int paymentId)
        {
            return 0;
        }
    }
}
