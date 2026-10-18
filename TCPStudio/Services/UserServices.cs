using System;
using System.Collections.Generic;
using System.Text;

namespace TCPStudio.Services
{
    public class UserServices
    {
        public string? Name { get; set; } = "A123";
        public List<UserModel> Users { get; set; } = [];
        public UserModel SetName(string[] UserArrs)
        {
            DateTime specificDate = new DateTime(2023, 1, 1);
            return new UserModel { Name = "123" };
        }
        public void EetName()
        {

        }
        public string? GetName()
        {
            return Name;
        }
        public void AddUser(UserModel user)
        {
            Users.Add(user);
        }
    }

    public class UserModel
    {
        public string? Name { get; set; }
    }
}
