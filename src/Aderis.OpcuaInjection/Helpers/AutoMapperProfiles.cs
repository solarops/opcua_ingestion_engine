using System.Text;
using Aderis.OpcuaInjection.Models;
using AutoMapper;


namespace Aderis.OpcuaInjection.Helpers;

public class AutoMapperProfiles : Profile
{
    public AutoMapperProfiles()
    {
        CreateMap<OpcClientConnectionDto, OpcClientConnection>()
            .ForMember(dest => dest.EncryptedPassword, opt => opt.MapFrom(src => 
                src.Password != null ? Encoding.UTF8.GetBytes(src.Password) : null))
            // .ForMember(dest => dest.BrowseExclusionFolders,
            //     opt => opt.MapFrom(src => src.BrowseExclusionFolders
            //         .Select(f => new BrowseExclusionFolder
            //         {
            //             ExclusionFolder = f,
            //             ConnectionOpcClientConnectionId = src.Id,
            //             OpcClientConnection = src
            //         })
            //         .ToList()));
            .ForMember(dest => dest.BrowseExclusionFolders, opt => opt.Ignore());
        
        // CreateMap<string, BrowseExclusionFolder>()
        //     .ForMember(dest => dest.ExclusionFolder, opt => opt.MapFrom(src => src));
        // CreateMap<List<string>, List<BrowseExclusionFolder>>()
        //     .ConvertUsing((src, dest, context) => src.Select(folder => context.Mapper.Map<BrowseExclusionFolder>(folder)).ToList());

        CreateMap<OpcClientConnection, OpcClientConnection>()
            .ForMember(dest => dest.Id, opt => opt.Ignore())
            .ForMember(dest => dest.BrowseExclusionFolders, opt => opt.Ignore());
    }
}
