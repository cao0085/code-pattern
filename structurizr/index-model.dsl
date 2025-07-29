YourSystem = softwareSystem "Name" {
    InitPage_1 = container "data" {
        description """
            描述該區域
        """
        Content1 = component "Content" {
            tags "tall-Element"
            description """
            描述該區域
            """
        }

        Content2 = component "Content2" {
            description """
            描述該區域
            """
        }

        InitPage_1.Content1 -> InitPage_1.Content2 "connection"
    }

    InitPage_2 = container "data2" {
        description """
            描述該區域
        """
        component Content {
            description """
            描述該區域
            """
        }
    }
}


YourSystem.InitPage_1 -> YourSystem.InitPage_2 "connection"