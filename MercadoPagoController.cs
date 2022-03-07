using Aplicacion.Controlador;
using log4net;
using Negocio.BIS;
using Servicios.Utiles;
using System;
using System.Web.Http;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Net.Http;
using System.Net;
using System.IO;
using System.Web.Configuration;
using Negocio.MODELOS_LINQ;

namespace WebApi.Controllers
{
    public class MercadoPagoController : ApiController
    {
        private readonly ILog _logger = LogManager.GetLogger(System.Environment.MachineName);

        [Route("api/PaymentNotification")]
        [HttpPost]
        public HttpResponseMessage PaymentNotification([FromBody] JObject jObject)
        {
            string conexion = WebConfigurationManager.ConnectionStrings["DelivegyDBConnectionString"].ConnectionString;

            try
            {
                _logger.Info("PaymentNotification: INPUT: " + JsonConvert.SerializeObject(jObject));

                JToken data;

                if (jObject.TryGetValue("action", out data))
                {
                    if (data.ToString() == "payment.created")
                    {
                        if (jObject.TryGetValue("data", out data))
                        {
                            string paymentId = data["id"].ToString();

                            _logger.Info("PaymentNotification: Payment id: " + paymentId);

                            //Se obtienen los detalles del pago con id informado
                            string url = "https://api.mercadopago.com/v1/payments/" + paymentId;

                            _logger.Info("PaymentNotification: URL: " + url);

                            string authorization = "Bearer " + WebConfigurationManager.AppSettings["MercadoPagoAccessToken"].ToString();

                            HttpWebResponse httpResponse = MercadoPagoControlador.GetResponseFromUrl(authorization, url, "GET");

                            if (httpResponse.StatusCode == HttpStatusCode.OK)
                            {
                                _logger.Info("PaymentNotification: Response Status: " + httpResponse.StatusCode);

                                StreamReader streamReader = new StreamReader(httpResponse.GetResponseStream());

                                string response = streamReader.ReadToEnd();

                                _logger.Info("PaymentNotification: Response from MP API: " + response);

                                jObject = (JObject)JsonConvert.DeserializeObject(response);

                                //Intentamos obtener el idPedido (external_reference)
                                if (jObject.TryGetValue("external_reference", out data))
                                {
                                    int idPedido = Convert.ToInt32(data);

                                    _logger.Info("PaymentNotification: External reference: " + idPedido);

                                    MercadoPagoIntegration integrationData = new MercadoPagoIntegration
                                    {
                                        IdPedido = idPedido,
                                        Request = url,
                                        Response = JsonConvert.SerializeObject(response),
                                        ExternalMercadoPagoId = paymentId,
                                        MercadoPagoExternalType = "Payment",
                                        TimeStamp = DateTime.Now
                                    };

                                    MercadoPagoControlador.Add(conexion, integrationData);

                                    //Obtenemos el estado del pago
                                    if (jObject.TryGetValue("status", out data))
                                    {
                                        string paymentStatus = data.ToString();

                                        _logger.Info("PaymentNotification: Payment Status: " + paymentStatus);

                                        //Estado del pago - de acuerdo a documentación de MP: https://www.mercadopago.com.ar/developers/es/reference/payments/_payments_id/get
                                        //pending: El usuario no completó el proceso de pago todavía.
                                        //approved: El pago fue aprobado y acreditado.
                                        //authorized: El pago fue autorizado pero no capturado todavía.
                                        //in_process: El pago está en revisión.
                                        //in_mediation: El usuario inició una disputa.
                                        //rejected: El pago fue rechazado. El usuario podría reintentar el pago.
                                        //cancelled: El pago fue cancelado por una de las partes o el pago expiró.
                                        //refunded: El pago fue devuelto al usuario.
                                        //charged_back: Se ha realizado un contracargo en la tarjeta de crédito del comprador.
                                        if (paymentStatus == "approved")
                                        {
                                            if (jObject.TryGetValue("transaction_details", out data))
                                            {
                                                decimal totalPaidAmount = Convert.ToDecimal(data["total_paid_amount"]);

                                                _logger.Info("PaymentNotification: Total paid amount: " + totalPaidAmount);

                                                //Entorno oEntorno = new Entorno();

                                                //oEntorno.ConexionDatos = Aplicacion.Utilidades.ConexionBase.ConexionCuentas();

                                                Pedido pedido = PedidoControlador.GetPedidoById(conexion, idPedido);

                                                if (pedido != null)
                                                {
                                                    if (totalPaidAmount >= (pedido.precioTotal + pedido.precioEnvio))
                                                    {
                                                        pedido.idEstado = 1;
                                                        pedido.PaidAmount = totalPaidAmount;
                                                        PedidoControlador.updateEstadoPedido(conexion, pedido);

                                                        string serverKeyFCM = WebConfigurationManager.AppSettings["serverKeyFCM"];
                                                        string urlFCM = WebConfigurationManager.AppSettings["urlFCM"];

                                                        NotificacionPushControlador.sendNotificationPushNuevoPedidoCliente(conexion, serverKeyFCM, urlFCM, pedido.id, pedido.idUsuario);
                                                        _logger.Info("PaymentNotification: sendNotificationPushNuevoPedidoCliente: idUsuario = " + pedido.idUsuario);

                                                        NotificacionPushControlador.sendNotificationPushNuevoPedidoRestaurante(conexion, serverKeyFCM, urlFCM, pedido.id, pedido.idRestaurante);
                                                        _logger.Info("PaymentNotification: sendNotificationPushNuevoPedidoRestaurante: idRestaurante = " + pedido.idRestaurante);
                                                    }
                                                    else
                                                    {
                                                        _logger.Error("PaymentNotification: Se informó un pago de $ " + totalPaidAmount + " que es menor al valor total del pedido con id " + idPedido);
                                                    }
                                                }
                                                else
                                                {
                                                    _logger.Error("PaymentNotification: No se encontró el pedido con id " + idPedido);
                                                    return new HttpResponseMessage(HttpStatusCode.BadRequest);
                                                }
                                            }
                                            else
                                            {
                                                _logger.Error("PaymentNotification: Error al intentar obtener el monto del pago id " + paymentId);
                                                return new HttpResponseMessage(HttpStatusCode.BadRequest);
                                            }
                                        }
                                    }
                                }
                                else
                                {
                                    _logger.Error("PaymentNotification: No se pudo obtener referencia externa para el pago id " + paymentId);
                                    return new HttpResponseMessage(HttpStatusCode.BadRequest);
                                }
                            }
                            else
                            {
                                _logger.Error("PaymentNotification: Response Status: " + httpResponse.StatusCode);
                                return new HttpResponseMessage(httpResponse.StatusCode);
                            }
                        }
                        else
                        {
                            _logger.Error("PaymentNotification: No fue posible obtener los datos del pago");
                            return new HttpResponseMessage(HttpStatusCode.BadRequest);
                        }
                    }
                }
            }
            catch (Exception e)
            {
                _logger.Error("PaymentNotification: " + e.Message);

                return new HttpResponseMessage(HttpStatusCode.InternalServerError);
            }
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }
}
