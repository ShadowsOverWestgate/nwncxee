namespace Sowx.Framework;

// Implement this and the host discovers, constructs (with constructor DI), and
// initializes it automatically. This is the only contract a contributor needs.
public interface IExtenderService
{
    void Initialize();
}
