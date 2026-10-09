// vkregs: build a compute pipeline from SPIR-V on the NVIDIA GPU (else the first) and print the driver's own
// executable statistics (register count, shared memory, binary size). Usage: vkregs a.spv binding:type... [b.spv ...]
// where type is the VkDescriptorType number; regcount.py reflects those off the module and is the way to call this.
#include <vulkan/vulkan.h>
#include <stdio.h>
#include <stdlib.h>

#define CK(x) do { VkResult r_ = (x); if (r_ != VK_SUCCESS) { fprintf(stderr, "%s -> %d\n", #x, r_); exit(1); } } while (0)

int main(int argc, char **argv)
{
    VkApplicationInfo ai = { VK_STRUCTURE_TYPE_APPLICATION_INFO, 0, "vkregs", 1, 0, 0, VK_API_VERSION_1_2 };
    VkInstanceCreateInfo ici = { VK_STRUCTURE_TYPE_INSTANCE_CREATE_INFO, 0, 0, &ai };
    VkInstance inst; CK(vkCreateInstance(&ici, 0, &inst));
    uint32_t n = 8; VkPhysicalDevice pds[8]; CK(vkEnumeratePhysicalDevices(inst, &n, pds));
    VkPhysicalDevice pd = 0;
    for (uint32_t i = 0; i < n; i++) { VkPhysicalDeviceProperties p; vkGetPhysicalDeviceProperties(pds[i], &p); if (p.vendorID == 0x10DE) pd = pds[i]; }
    if (!pd) pd = pds[0];
    float pr = 1; VkDeviceQueueCreateInfo qci = { VK_STRUCTURE_TYPE_DEVICE_QUEUE_CREATE_INFO, 0, 0, 0, 1, &pr };
    VkPhysicalDevicePipelineExecutablePropertiesFeaturesKHR pe = { VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_PIPELINE_EXECUTABLE_PROPERTIES_FEATURES_KHR, 0, VK_TRUE };
    VkPhysicalDeviceFeatures2 f2 = { VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_FEATURES_2, &pe };
    vkGetPhysicalDeviceFeatures2(pd, &f2);
    f2.features.robustBufferAccess = VK_FALSE;
    const char *ext[] = { "VK_KHR_pipeline_executable_properties" };
    VkDeviceCreateInfo dci = { VK_STRUCTURE_TYPE_DEVICE_CREATE_INFO, &f2, 0, 1, &qci, 0, 0, 1, ext, 0 };
    VkDevice dev; CK(vkCreateDevice(pd, &dci, 0, &dev));
    PFN_vkGetPipelineExecutablePropertiesKHR gp = (void *)vkGetDeviceProcAddr(dev, "vkGetPipelineExecutablePropertiesKHR");
    PFN_vkGetPipelineExecutableStatisticsKHR gs = (void *)vkGetDeviceProcAddr(dev, "vkGetPipelineExecutableStatisticsKHR");
    for (int a = 1; a < argc; a++)
    {
        FILE *f = fopen(argv[a], "rb"); if (!f) { perror(argv[a]); continue; }
        fseek(f, 0, SEEK_END); long sz = ftell(f); fseek(f, 0, SEEK_SET);
        uint32_t *code = malloc(sz); fread(code, 1, sz, f); fclose(f);
        VkShaderModuleCreateInfo smci = { VK_STRUCTURE_TYPE_SHADER_MODULE_CREATE_INFO, 0, 0, sz, code };
        VkShaderModule sm; CK(vkCreateShaderModule(dev, &smci, 0, &sm));
        // descriptor-set layouts are not validated against the module here; the driver compiles regardless
        VkDescriptorSetLayoutBinding lb[64]; uint32_t nb = 0;
        for (int b = a + 1; b < argc && nb < 64; b++)
        {
            unsigned bi, ty; if (sscanf(argv[b], "%u:%u", &bi, &ty) != 2) break;
            lb[nb++] = (VkDescriptorSetLayoutBinding){ bi, (VkDescriptorType)ty, 1, VK_SHADER_STAGE_COMPUTE_BIT, 0 };
        }
        VkDescriptorSetLayoutCreateInfo dl = { VK_STRUCTURE_TYPE_DESCRIPTOR_SET_LAYOUT_CREATE_INFO, 0, 0, nb, lb };
        VkDescriptorSetLayout dsl; CK(vkCreateDescriptorSetLayout(dev, &dl, 0, &dsl));
        VkPipelineLayoutCreateInfo plci = { VK_STRUCTURE_TYPE_PIPELINE_LAYOUT_CREATE_INFO, 0, 0, 1, &dsl };
        VkPipelineLayout pl; CK(vkCreatePipelineLayout(dev, &plci, 0, &pl));
        VkComputePipelineCreateInfo cpci = { VK_STRUCTURE_TYPE_COMPUTE_PIPELINE_CREATE_INFO, 0,
            VK_PIPELINE_CREATE_CAPTURE_STATISTICS_BIT_KHR,
            { VK_STRUCTURE_TYPE_PIPELINE_SHADER_STAGE_CREATE_INFO, 0, 0, VK_SHADER_STAGE_COMPUTE_BIT, sm, "main" }, pl };
        VkPipeline pipe; CK(vkCreateComputePipelines(dev, 0, 1, &cpci, 0, &pipe));
        VkPipelineInfoKHR pi = { VK_STRUCTURE_TYPE_PIPELINE_INFO_KHR, 0, pipe };
        uint32_t ne = 0; gp(dev, &pi, &ne, 0);
        printf("%s:", argv[a]);
        for (uint32_t e = 0; e < ne; e++)
        {
            VkPipelineExecutableInfoKHR ei = { VK_STRUCTURE_TYPE_PIPELINE_EXECUTABLE_INFO_KHR, 0, pipe, e };
            uint32_t ns = 0; gs(dev, &ei, &ns, 0);
            VkPipelineExecutableStatisticKHR st[32];
            for (uint32_t s = 0; s < 32; s++) st[s] = (VkPipelineExecutableStatisticKHR){ VK_STRUCTURE_TYPE_PIPELINE_EXECUTABLE_STATISTIC_KHR };
            if (ns > 32) ns = 32;
            gs(dev, &ei, &ns, st);
            for (uint32_t s = 0; s < ns; s++)
            {
                printf("  %s=", st[s].name);
                switch (st[s].format)
                {
                case VK_PIPELINE_EXECUTABLE_STATISTIC_FORMAT_BOOL32_KHR: printf("%u", st[s].value.b32); break;
                case VK_PIPELINE_EXECUTABLE_STATISTIC_FORMAT_INT64_KHR: printf("%lld", (long long)st[s].value.i64); break;
                case VK_PIPELINE_EXECUTABLE_STATISTIC_FORMAT_UINT64_KHR: printf("%llu", (unsigned long long)st[s].value.u64); break;
                default: printf("%g", st[s].value.f64);
                }
            }
        }
        printf("\n");
        a += nb;
        vkDestroyPipeline(dev, pipe, 0); vkDestroyPipelineLayout(dev, pl, 0); vkDestroyShaderModule(dev, sm, 0); free(code);
    }
    return 0;
}
