workspace "YourProjectName" {

    // **
    !identifiers hierarchical

    model {
        // Include your model files here
        // folderName_xxx/FileName_xxx

        !include index-model.dsl
    }

    views {
        styles {
            element "Component" {
                width  520
                height 500
            }
            element "tall-Element"{
                width  600
                height 700             
            }
        }
        // Include your views files here
        // folderName_xxx/FileName_xxx

        !include index-view.dsl
    }
}