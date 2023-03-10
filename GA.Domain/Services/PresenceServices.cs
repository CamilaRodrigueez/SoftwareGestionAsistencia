using Common.Utils.Exceptions;
using Common.Utils.Resorces;
using GA.Domain.DTO;
using GA.Domain.DTO.Excuse;
using GA.Domain.DTO.Notification;
using GA.Domain.DTO.Presence;
using GA.Domain.Services.Interface;
using Infraestructure.Core.UnitOfWork.Interface;
using Infraestructure.Entity.Model.GA;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MailKit.Net.Smtp;
using MailKit;
using MimeKit;
using static Common.Utils.Constant.Const;
using Infraestructure.Entity.Model;
using GA.Domain.DTO.Class;
using GA.Domain.DTO.Ficha;
using Microsoft.AspNetCore.Mvc;
using Common.Utils.Constant;
using Common.Utils.Enums;

namespace GA.Domain.Services
{
    public class PresenceServices : IPresenceServices
    {
        #region Attributes
        private readonly IUnitOfWork _unitOfWork;
        private readonly IUserServices _userServices;
        private readonly IExcuseServices _excuseServices;
        private readonly INotificationServices _notificationServices;
        private readonly IFichaServices _fichaServices;


        //private readonly IConfiguration _configuration;
        #endregion

        #region Builder
        public PresenceServices(IUnitOfWork unitOfWork,
                                IUserServices userServices,
                                IExcuseServices excuseServices,
                                INotificationServices notificationServices,
                                IFichaServices fichaServices)
        {
            _unitOfWork = unitOfWork;
            _userServices = userServices;
            _excuseServices = excuseServices;
            _notificationServices = notificationServices;
            _fichaServices = fichaServices;
            //_configuration = configuration;
        }
        #endregion

        #region Methods

        public List<DetailPresenceDto> GetPresenceDetailStudents(ConsultPresenceDto consult)
        {
            IEnumerable<PresenceEntity> result = GetPresenceEntities(consult);

            var list = result.Select(x => new DetailPresenceDto()
            {
                Excuse = x.Excuse,
                IdUser = x.IdUser,
                NoExcuse = x.NoExcuse,
                IdClass = x.IdClass,
                IdFicha = x.IdFicha,
                Identification = x.UserEntity.Identification,
                IdPresence = x.Id,
                ExcuseDate = x.Date,
                FullName = x.UserEntity?.FullName,
                StrClass = x.ClassEntity.StrClass,
                StrDate = x.Date.ToString("MMMM - dd, yyyy").ToUpper(),
                Ficha = $"{x.FichaEntity.Nombre} ({x.FichaEntity.Num_Ficha})"
            }).ToList();

            return list;
        }

        private IEnumerable<PresenceEntity> GetPresenceEntities(ConsultPresenceDto consult)
        {
            return _unitOfWork.PresenceRepository.FindAll(x => x.IdFicha == consult.IdFicha
                                                            && x.IdClass == (consult.IdClass == 0 ? x.IdClass : consult.IdClass)
                                                            && x.IdUser == (consult.IdUser == 0 ? x.IdUser : consult.IdUser)
                                                            && (x.Date >= consult.StartDate && x.Date <= consult.EndDate),
                                                           u => u.UserEntity,
                                                           c => c.ClassEntity,
                                                           f => f.FichaEntity);
        }


        public List<ResultPresenceDto> GetAllPresence(ConsultPresenceDto consult)
        {
            IEnumerable<PresenceEntity> result = GetPresenceEntities(consult);

            List<ResultPresenceDto> resultGroup = (from p in result
                                                   group p by p.IdUser into grupo
                                                   select new ResultPresenceDto
                                                   {
                                                       IdUser = grupo.Key,
                                                       IdFicha = consult.IdFicha,
                                                       IdClass = consult.IdClass,
                                                       Identification = grupo.Select(x => x.UserEntity.Identification).First(),
                                                       FullName = grupo.Select(x => x.UserEntity.FullName).First(),
                                                       Count = grupo.Count()
                                                   }).ToList();


            return resultGroup;
        }

        public List<StudentPresenceDto> ConsultStudentsPresence(ConsultPresenceDto consult)
        {
            List<UserEntity> listStudets = _unitOfWork.UserRepository.GetTypeUserByFicha(consult.IdFicha, 
                                                                                        (int)Enums.TypeUser.Aprendiz);
            if (!listStudets.Any())
                throw new BusinessException(GeneralMessages.NoStudentsSelected);

            List<StudentPresenceDto> listPresences = listStudets.Select(x => new StudentPresenceDto()
            {
                IdUser = x.IdUser,
                IdFicha = consult.IdFicha,
                IdClass = consult.IdClass,
                ExcuseDate = consult.StartDate,
                FullName = x.FullName,
                Identification = x.Identification,
            }).ToList();

            List<ExcuseDto> excuses = _excuseServices.GetExcuseByDate(dateExcuse: consult.StartDate,
                                                                      idFicha: consult.IdFicha,
                                                                      idClass: consult.IdClass);
            foreach (var item in excuses)
            {
                var presence = listPresences.FirstOrDefault(x => x.IdUser == item.IdUser);
                listPresences.Remove(presence);

                presence.Excuse = true;
                listPresences.Add(presence);
            }

            consult.EndDate = consult.StartDate;
            List<PresenceEntity> presences = GetPresenceEntities(consult).ToList();
            foreach (var item in presences)
            {
                var presence = listPresences.FirstOrDefault(x => x.IdUser == item.IdUser);
                listPresences.Remove(presence);

                if (item.Excuse)
                {
                    presence.NoExcuse = false;
                    presence.Excuse = true;
                }
                else
                {
                    presence.Excuse = false;
                    presence.NoExcuse = true;
                }

                listPresences.Add(presence);
            }

            return listPresences;
        }

        public async Task<ResponseDto> InsertPresence(List<StudentPresenceDto> listStudents, int idUserInstructor)
        {
            ResponseDto response = new ResponseDto();

            int idFicha = listStudents.First().IdFicha;
            int idClass = listStudents.First().IdClass;
            DateTime fecha = listStudents.First().ExcuseDate;

            using (var transaction = _unitOfWork.BeginTransaction())
            {
                try
                {
                    IEnumerable<PresenceEntity> presences = GetPresenceEntities(new ConsultPresenceDto()
                    {
                        IdClass = idClass,
                        IdFicha = idFicha,
                        StartDate = fecha,
                        EndDate = fecha,
                    });
                    if (presences.Any())
                    {
                        _unitOfWork.PresenceRepository.Delete(presences);
                        await _unitOfWork.Save();
                    }

                    if (listStudents.Any(x => x.Excuse || x.NoExcuse))
                    {
                        List<PresenceEntity> presencesList = listStudents.Where(x => x.Excuse || x.NoExcuse).Select(x => new PresenceEntity()
                        {
                            Excuse = x.Excuse,
                            NoExcuse = x.NoExcuse,
                            IdClass = idClass,
                            IdFicha = idFicha,
                            IdUser = x.IdUser,
                            RegistrationDate = DateTime.Now,
                            Date = fecha,
                        }).ToList();

                        _unitOfWork.PresenceRepository.Insert(presencesList);
                        await _unitOfWork.Save();
                    }

                    response.IsSuccess = true;
                    response.Message = GeneralMessages.ItemInserted;
                    response.Result = await ValidatePresenceNotification(listStudents, idUserInstructor);
                    transaction.Commit();
                }
                catch (Exception)
                {
                    response.Message = GeneralMessages.ItemNoInserted;
                    transaction.Rollback();
                }
            }

            return response;
        }

        private async Task<List<NotificationDto>> ValidatePresenceNotification(List<StudentPresenceDto> listStudents, int idUserInstructor)
        {
            int idFicha = listStudents.First().IdFicha;
            int idClass = listStudents.First().IdClass;

            IEnumerable<PresenceEntity> presences = _unitOfWork.PresenceRepository.FindAll(x => x.IdFicha == idFicha
                                                                                             && x.IdClass == idClass,
                                                                                           c => c.ClassEntity,
                                                                                           u => u.UserEntity);

            List<PresenceEntity> presenceSelect = (from t in presences
                                                   where listStudents.Any(x => x.IdUser == t.IdUser)
                                                   select t).ToList();

            //Agrupo y seleciono solamente los que tengan más de 3 registros.
            var resultGroup = (from p in presenceSelect
                               group p by p.IdUser into grupo
                               select new
                               {
                                   IdUser = grupo.Key,
                                   Identification = grupo.Select(x => x.UserEntity.Identification).First(),
                                   FullName = grupo.Select(x => x.UserEntity.FullName).First(),
                                   Clase = grupo.Select(x => x.ClassEntity.StrClass).First(),
                                   Email = grupo.Select(x => x.UserEntity.Email).First(),
                                   Count = grupo.Count()
                               }).Where(x => x.Count >= 3).ToList();

            var notifications = resultGroup.Select(x => new NotificationDto()
            {
                IdUser = idUserInstructor,
                Title = "Inasistencias",
                Description = String.Format(GeneralMessages.NotificationPresences, x.FullName, x.Count, x.Clase),
            }).ToList();

            if (notifications.Any())
            {
                await _notificationServices.InsertNotification(notifications);

                UserEntity userInstructor = _userServices.GetUser(idUserInstructor);
                var listStudentsInasistencia = resultGroup.Select(x => new ResultPresenceDto()
                {
                    IdUser = x.IdUser,
                    Identification = x.Identification,
                    FullName = x.FullName,
                    Email = x.Email,
                    Clase = x.Clase,
                    Count = x.Count,

                }).ToList();
                SendEmail(userInstructor, listStudentsInasistencia);


            }

            return notifications;
        }


        #endregion

        #region SendEmail
        //Servicio Enviar Correos al instructor y a los aprendices con asistencias > 3
        private void SendEmail(UserEntity userEntityInstructor, List<ResultPresenceDto> listStudents)
        {
            string request = "";
            if (userEntityInstructor != null && listStudents != null)
            {
                foreach (var item in listStudents)
                {
                    MimeMessage mimeMessage = new MimeMessage();
                    mimeMessage.From.Add(new MailboxAddress("Sistema de Gestión de Asistencias", Email.GmailUser));
                    mimeMessage.To.Add(new MailboxAddress("Destino", userEntityInstructor.Email));
                    mimeMessage.To.Add(new MailboxAddress("Destino", item.Email));
                    mimeMessage.Subject = "¡Alerta de Inasistencias!";
                    BodyBuilder cuerpoMensaje = new BodyBuilder();
                    request = "<!DOCTYPE html>" +
                              "<html>" +
                              "<head></head>" +
                              "<body>" +
                              " <div>" +
                              "<h1>¡Alerta de Inasistencias!</h1>" +
                              "<h4>Este Correo es expresamente informativo y genérico </h4>" +
                              "<p>Cordial Saludo.<br>" +
                              "Instructor: {0} <br>" +
                              "Aprendiz: {1} <br></p>" + "" +
                              "<p>Atentamente les informamos que {2} como aprendiz tiene {3} inasistencias consecutivas <br>" +
                              "en la clase de {4}.</p>" +
                              "<br>" +
                              "<p>Es momento de empezar con el debito proceso de seguimiento a este aprendiz.</p><br>" +
                              "<P><b>NOTA:</b> Este correo ha sido generado automaticamente, por favor no responda ni envie copia  a la cuenta remitente. </p>" +
                              "</body>" +
                              "</html>";
                    request = string.Format(request, userEntityInstructor.Name + userEntityInstructor.LastName, item.FullName, item.FullName, item.Count, item.Clase);
                    cuerpoMensaje.HtmlBody = request;
                    mimeMessage.Body = cuerpoMensaje.ToMessageBody();

                    try
                    {
                        SmtpClient smtpClient = new SmtpClient();
                        smtpClient.CheckCertificateRevocation = false;
                        smtpClient.Connect(Email.ServidorGmail, Email.PuertoGmail, MailKit.Security.SecureSocketOptions.StartTls);
                        smtpClient.Authenticate(Email.GmailUser, Email.GmailPassword);
                        smtpClient.Send(mimeMessage);
                        smtpClient.Disconnect(true);
                    }
                    catch (Exception)
                    {

                        throw;
                    }
                }



            }


        }
        #endregion


        public List<DetailPresenceDto> GetPresenceDetailStudents_(int idUser)
        {
            List<PresenceEntity> result = GetAllPresence_(idUser);

            var list = result.Select(x => new DetailPresenceDto()
            {
                Excuse = x.Excuse,
                IdUser = x.IdUser,
                NoExcuse = x.NoExcuse,
                IdClass = x.IdClass,
                IdFicha = x.IdFicha,
                Identification = x.UserEntity.Identification,
                IdPresence = x.Id,
                ExcuseDate = x.Date,
                FullName = x.UserEntity?.FullName,
                StrClass = x.ClassEntity.StrClass,
                StrDate = x.Date.ToString("MMMM - dd, yyyy").ToUpper(),
                Ficha = $"{x.FichaEntity.Nombre} ({x.FichaEntity.Num_Ficha})"
            }).ToList();

            return list;
        }
        private List<PresenceEntity> GetAllPresence_(int idUser)
        {
            List<ConsultFichaDto> list = _fichaServices.GetAllFichasClass(idUser);

            PresenceEntity result = new PresenceEntity();
            var listInasistenciasUser = new List<PresenceEntity>();

            foreach (var item in list)
            {
                List<ClassDto> clasesDeLaFicha = item.ListClass;
                foreach (var item2 in clasesDeLaFicha)
                {
                    result = GetPresenceEntities_(item.IdFicha, item2.IdClass, idUser);
                    if (result != null)
                    {
                        listInasistenciasUser.Add(result);
                    }
                }
            }

            return listInasistenciasUser;
        }
        private PresenceEntity GetPresenceEntities_(int idFicha, int idClase, int idUser)
        {
            PresenceEntity result = null;

            result = _unitOfWork.PresenceRepository.FirstOrDefault(x => x.IdFicha == idFicha
                                                        && x.IdClass == idClase
                                                        && x.IdUser == idUser,
                                                        u => u.UserEntity,
                                                        c => c.ClassEntity,
                                                        f => f.FichaEntity);
          

            return result;
        }

    }
}
