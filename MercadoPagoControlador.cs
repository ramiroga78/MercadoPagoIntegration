using log4net;
using MercadoPago.Client.Common;
using MercadoPago.Client.Preference;
using MercadoPago.Config;
using MercadoPago.Resource.Preference;
using Negocio.BIS;
using Negocio.DATA;
using Negocio.MODELOS_LINQ;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.IO;
using System.Net;
using System.Web.Configuration;

namespace Aplicacion.Controlador
{
    public class MercadoPagoControlador
    {
        public static DataTable GetPaymentURL(string conexion, int idUsuario, Pedido pedido)
        {
            try
            {
                Usuario usuario = UsuarioControlador.getUsuarioById(conexion, idUsuario);

                UbicacionUsuario ubicacionUsuario = UbicacionUsuarioControlador.GetUbicacionUsuarioById(conexion, pedido.id_ubicacion_usuario);

                List<DetallePedido> detallePedidoList = DetallePedidoControlador.DetallePedidoIdPedido(conexion, pedido.id);

                string backendURL = WebConfigurationManager.AppSettings["BackendURL"].ToString();
                MercadoPagoConfig.AccessToken = WebConfigurationManager.AppSettings["MercadoPagoAccessToken"].ToString();

                // Crea el request con múltiples ítems
                var preferenceRequest = new PreferenceRequest
                {
                    ExternalReference = pedido.id.ToString(),
                    Items = new List<PreferenceItemRequest>(),
                    Shipments = new PreferenceShipmentsRequest
                    {
                        Cost = pedido.precioEnvio, //Costo especificado del envío
                        Mode = "not_specified"
                    },
                    Payer = new PreferencePayerRequest
                    {
                        Name = usuario.nombre,
                        Surname = "",
                        Email = usuario.mail,
                        Phone = new PhoneRequest
                        {
                            AreaCode = "",
                            Number = usuario.telefono
                        },
                        Identification = new IdentificationRequest
                        {
                            Type = "DNI",
                            Number = usuario.DNI
                        },
                        Address = new AddressRequest
                        {
                            ZipCode = "",
                            StreetName = ubicacionUsuario.ubicacion,
                            StreetNumber = ""
                        }
                    },
                    PaymentMethods = new PreferencePaymentMethodsRequest
                    {
                        ExcludedPaymentTypes = new List<PreferencePaymentTypeRequest>
                    {
                        new PreferencePaymentTypeRequest
                        {
                            Id = "ticket",
                        },
                    },
                        Installments = 1,
                    },
                    StatementDescriptor = "Company_Name",
                    BinaryMode = true,
                    Expires = true,
                    ExpirationDateTo = DateTime.Now.AddDays(1),
                    NotificationUrl = backendURL + "/api/PaymentNotification"
                };

                foreach (DetallePedido item in detallePedidoList)
                {
                    PreferenceItemRequest preferenceItemRequest = new PreferenceItemRequest
                    {
                        Id = item.idMenu.ToString(),
                        Title = item.descripcion,
                        Description = item.detalle,
                        PictureUrl = backendURL + "/getimagen.aspx?id=" + item.idMenu + "&entidad=MF",
                        CategoryId = "others", //De acuerdo a la documentación de MP: https://api.mercadopago.com/item_categories
                        Quantity = item.cantidad,
                        CurrencyId = "ARS",
                        UnitPrice = item.precioFinal / item.cantidad,
                    };

                    preferenceRequest.Items.Add(preferenceItemRequest);
                }

                var client = new PreferenceClient();

                Preference preference = client.Create(preferenceRequest);

                MercadoPagoIntegration integrationData = new MercadoPagoIntegration
                {
                    IdPedido = pedido.id,
                    Request = JsonConvert.SerializeObject(preferenceRequest),
                    Response = JsonConvert.SerializeObject(preference),
                    ExternalMercadoPagoId = preference.Id,
                    MercadoPagoExternalType = "Preference",
                    TimeStamp = DateTime.Now
                };

                Add(conexion, integrationData);

                return ConvertPreferenceToDataTable(preference);
            }
            catch (Exception e)
            {
                throw e;
            }
        }

        public static void RefundPayment(string conexion, string authorization, int idPedido)
        {
            try
            {
                ILog _logger = LogManager.GetLogger(System.Environment.MachineName);

                string paymentId = MercadoPagoIntegrationDATA.GetPaymentIdByIdPedido(conexion, idPedido);

                string url = "https://api.mercadopago.com/v1/payments/" + paymentId + "/refunds";

                _logger.Info("RefundPayment: URL: " + url);

                HttpWebResponse httpResponse = GetResponseFromUrl(authorization, url, "POST");

                if (httpResponse.StatusCode == HttpStatusCode.OK || httpResponse.StatusCode == HttpStatusCode.Created)
                {
                    _logger.Info("RefundPayment: Response Status: " + httpResponse.StatusCode);

                    StreamReader streamReader = new StreamReader(httpResponse.GetResponseStream());

                    string response = streamReader.ReadToEnd();

                    _logger.Info("RefundPayment: Response from MP API: " + response);

                    MercadoPagoIntegration integrationData = new MercadoPagoIntegration
                    {
                        IdPedido = idPedido,
                        Request = url,
                        Response = JsonConvert.SerializeObject(response),
                        ExternalMercadoPagoId = paymentId,
                        MercadoPagoExternalType = "Refund",
                        TimeStamp = DateTime.Now
                    };

                    Add(conexion, integrationData);
                }
                else
                {
                    _logger.Error("RefundPayment: Ocurrió un error al procesar el reembolso del pedido " + idPedido);
                }
            }
            catch (Exception ex)
            {
                throw ex;
            }
        }

        public static HttpWebResponse GetResponseFromUrl(string authorizarion, string url, string method)
        {
            HttpWebRequest httpRequest = (HttpWebRequest)WebRequest.Create(url);

            httpRequest.Method = method;
            httpRequest.Accept = "application/json";
            httpRequest.Headers["Authorization"] = authorizarion;

            return (HttpWebResponse)httpRequest.GetResponse();
        }

        public static void Add(string conexion, MercadoPagoIntegration integrationData)
        {
            MercadoPagoIntegrationDATA.Add(conexion, integrationData);
        }

        private static DataTable ConvertPreferenceToDataTable(Preference preference)
        {
            DataTable dataTable = new DataTable();

            PropertyDescriptorCollection props = TypeDescriptor.GetProperties(typeof(Preference));

            foreach (PropertyDescriptor p in props)
            {
                dataTable.Columns.Add(p.Name, Nullable.GetUnderlyingType(p.PropertyType) ?? p.PropertyType);
            }

            DataRow row = dataTable.NewRow();

            foreach (PropertyDescriptor prop in props)
            {
                row[prop.Name] = prop.GetValue(preference) ?? DBNull.Value;
            }

            dataTable.Rows.Add(row);

            return dataTable;
        }
    }
}
